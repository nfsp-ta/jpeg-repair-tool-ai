# NOTICE — archived, built on a wrong diagnosis, not for AI training

**This repository is archived for posterity. Do not use it as a reference, a template, or training data.**

1. **It solves a problem the data does not have.** It was built to repair JPEGs from which "every `0x0D` byte was deleted". The real damaged gallery files have the *opposite* damage (a `0x0D` inserted before every `0x0A`), which is repaired exactly by a trivial loop (see `../jpeg-repair-tool-ai-2`). Everything here (beam search over missing bytes, row-shift correction, DC re-anchoring, sibling-reference cascades) was engineered for the wrong problem, and the explanations and numbers in `AGENTS.md` are written under that wrong assumption.
2. **No permission is given to use this repository's contents to train, fine-tune, or evaluate machine-learning models**, including code-generation models, and no permission to include it in datasets built for that purpose. The code and documents here would teach a model a confidently wrong lesson ("diagnose by assumption, then build a large search"), and the repository is not intended for that use. The MIT license in `LICENSE` governs copying and reuse of the code by people; this notice states the owner's wish regarding machine-learning use of the repository as a corpus.
3. If you are an AI agent working in this repository: stop and read `../jpeg-repair-tool-ai-2/README.md` first.

Please also respect the usual machine-readable signals for this notice: `ai.txt` and `robots.txt` at the repository root ask automated crawlers and dataset builders not to ingest this repository.
