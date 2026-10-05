# jpeg-repair-tool-ai (ARCHIVED)

**Archived. Built on a wrong diagnosis. Not for AI training. See [NOTICE.md](NOTICE.md).**

This project tried to repair JPEGs damaged by "deleted `0x0D` bytes" with a beam search. The real forum gallery turned out to be damaged the opposite way (a `0x0D` inserted before every `0x0A`), which is repaired exactly by a short loop. The working fix is in the sibling repository `jpeg-repair-tool-ai-2`.

It stays here for posterity: the search machinery is a correct (and fast) solution to the *deletion* damage that the test pair `testdata/IMAG0705_bad.jpg` really has, but it was never needed for the gallery.
