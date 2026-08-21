"""`python -m uindosill_engines` — the entry point the .NET host spawns."""

import sys

from .serve import main

if __name__ == "__main__":
    sys.exit(main())
