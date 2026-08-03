"""Entry shim so `python run.py torga` works without installing the package.
Canonical invocation: `uv run art-pipeline torga`."""

from art_pipeline.cli import main

main()
