"""Local, deterministic validation harness for the C# backtester (AUDIT.md, README "Validation gates").

The C# engine stays the only engine: `Backtester.Cli validate-export` writes dev-window bundles, and this package turns them
into `validation_report.json` files with every hard gate's value, threshold and verdict. Nothing here touches the network
(`netguard`) or reads holdout rows (`data`). Run everything with `python -m validation run`.
"""

SCHEMA_VERSION = 1
