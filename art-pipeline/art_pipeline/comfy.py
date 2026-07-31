"""Thin client for ComfyUI's native HTTP API.

The server is a plain HTTP surface: POST /prompt queues an API-format
workflow, /ws streams progress, /history/{id} holds results, /view serves
output files. Nothing here knows about characters or stages — that's cli.py.
"""

from __future__ import annotations

import json
import time
import uuid
from pathlib import Path

import httpx


class ComfyError(RuntimeError):
    pass


class ComfyClient:
    def __init__(self, url: str):
        self.url = url.rstrip("/")
        self.client_id = uuid.uuid4().hex
        self._http = httpx.Client(base_url=self.url, timeout=30.0)

    def alive(self) -> bool:
        try:
            return self._http.get("/system_stats").status_code == 200
        except httpx.HTTPError:
            return False

    def upload_image(self, path: Path) -> str:
        """Upload into ComfyUI's input store; returns the name LoadImage expects."""
        with path.open("rb") as f:
            r = self._http.post(
                "/upload/image",
                files={"image": (path.name, f)},
                data={"overwrite": "true"},
            )
        r.raise_for_status()
        return r.json()["name"]

    def queue(self, workflow: dict) -> str:
        r = self._http.post(
            "/prompt", json={"prompt": workflow, "client_id": self.client_id}
        )
        if r.status_code != 200:
            raise ComfyError(f"queue rejected ({r.status_code}): {r.text[:2000]}")
        return r.json()["prompt_id"]

    def wait(self, prompt_id: str, timeout_s: float = 1800) -> dict:
        """Follow the websocket for progress; fall back to history polling.

        Returns the /history entry for the prompt (outputs included).
        """
        try:
            self._wait_ws(prompt_id, timeout_s)
        except Exception as e:  # ws is best-effort; history is the truth
            print(f"  (websocket unavailable, polling: {e})")
        return self._wait_history(prompt_id, timeout_s)

    def _wait_ws(self, prompt_id: str, timeout_s: float) -> None:
        from websockets.sync.client import connect

        ws_url = self.url.replace("http", "ws", 1) + f"/ws?clientId={self.client_id}"
        deadline = time.monotonic() + timeout_s
        with connect(ws_url, max_size=None) as ws:
            while time.monotonic() < deadline:
                msg = ws.recv(timeout=deadline - time.monotonic())
                if isinstance(msg, bytes):
                    continue  # preview image frames
                event = json.loads(msg)
                data = event.get("data", {})
                if data.get("prompt_id") not in (None, prompt_id):
                    continue
                if event["type"] == "executing" and data.get("node"):
                    print(f"  running node {data['node']}")
                if event["type"] == "execution_error":
                    raise ComfyError(f"execution error: {json.dumps(data)[:2000]}")
                if event["type"] == "executing" and data.get("node") is None:
                    return  # finished
                if event["type"] == "execution_success":
                    return
        raise ComfyError(f"timed out after {timeout_s}s waiting for {prompt_id}")

    def _wait_history(self, prompt_id: str, timeout_s: float) -> dict:
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            r = self._http.get(f"/history/{prompt_id}")
            r.raise_for_status()
            entry = r.json().get(prompt_id)
            if entry:
                status = entry.get("status", {})
                if status.get("status_str") == "error":
                    raise ComfyError(
                        f"prompt failed: {json.dumps(status)[:2000]}"
                    )
                return entry
            time.sleep(2)
        raise ComfyError(f"no history for {prompt_id} after {timeout_s}s")

    def download_output(self, filename: str, subfolder: str, dest: Path) -> Path:
        r = self._http.get(
            "/view",
            params={"filename": filename, "subfolder": subfolder, "type": "output"},
        )
        r.raise_for_status()
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(r.content)
        return dest


def find_glb_outputs(history_entry: dict) -> list[tuple[str, str]]:
    """(filename, subfolder) pairs for every .glb the prompt produced."""
    found = []
    for node_output in history_entry.get("outputs", {}).values():
        for value in node_output.values():
            if not isinstance(value, list):
                continue
            for item in value:
                if isinstance(item, dict) and str(item.get("filename", "")).endswith(
                    ".glb"
                ):
                    found.append((item["filename"], item.get("subfolder", "")))
    return found
