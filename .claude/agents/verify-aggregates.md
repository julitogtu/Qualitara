---
description: Cross-check /pulse against the independent Python oracle for a fixed set of cases
---
Verify the API against the oracle. Do not edit any code or test while doing this.

1. Make sure the API is running against the seeded database (start it if needed).
2. Run `python tools/verify_aggregates.py $ARGUMENTS`.
3. Report the result table as-is: case · oracle value · API value · MATCH/MISMATCH.
4. For any MISMATCH, investigate and explain the likely cause, but do not fix anything and
   never "fix" a golden by copying the API's output. Wait for my decision.