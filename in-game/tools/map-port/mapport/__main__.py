import sys

# `python -m mapport cs <files or folders>` adds the CS map spec to existing ports.
if len(sys.argv) > 1 and sys.argv[1] == "cs":
    from .csmap import main as cs_main
    sys.exit(cs_main(sys.argv[2:]))

from .cli import main

sys.exit(main())
