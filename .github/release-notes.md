## Which file do I want?

**Most people: `UindosillDesktop-win-Setup.exe`.** The desktop application, carrying the CPU and
Vulkan backends and the bundled Python inside it. Vulkan covers AMD, Intel and NVIDIA cards alike,
so this is the right download unless you specifically want CUDA.

**`UindosillDesktop-win-cuda-Setup.exe`** is the same application with NVIDIA's CUDA runtime added,
which makes it roughly a gigabyte larger. Take it only on an NVIDIA card. Whichever you install,
the application remembers which flavour it came from and keeps updating from that one, so a CUDA
install is never quietly moved onto the default build.

**Both are unsigned**, so Windows will warn you about an unknown publisher and you will have to
click through it. That is a decision recorded in `docs/PHASES.md` rather than an oversight: v1.0
ships without a signing identity.

Only 64-bit Windows gets an installer. There is no ARM64 build, because upstream publishes no ARM64
speech-recognition binary and an installer that cannot transcribe is worse than none.

## Using it from the command line

**`uindosill-cli-win-x64.zip`** is the command-line tool. It is deliberately not inside either
installer, so unzip it wherever you like and put it on your PATH yourself.

**`uindosill-python-win-x64.zip` is for command-line users only.** Both installers already carry
this interpreter inside themselves, so you need this one only if you are using the CLI, and only if
you want `uindosill diarise` or `transcribe --translate` to work. Unzip it into
`%LOCALAPPDATA%\Uindosill` so that a `python` folder sits directly inside. Without it those two
verbs refuse to run and say why; everything else in the CLI works without it.

## Optional: NVIDIA acceleration for speaker labelling

**You do not download these files yourself.** The `uindosill-python-cuda-win-x64.zip.001` to `.004`
files and `manifest.json` are the CUDA build of PyTorch, and the application fetches them for you:
open **Settings, Advanced** and press the button. The row is offered only on a machine whose driver
actually reports CUDA, and pressing it downloads about 1.8 GB, checks every part against the digest
this release pins, and unpacks it. A connection that drops resumes where it stopped instead of
starting again.

They sit here as four parts rather than one file because a single 1.8 GB asset leaves too little
room under GitHub's 2 GiB per-file limit. That is a packaging detail, not something to do by hand.

On one ten-minute recording this was about 13 times faster than the processor and produced exactly
the same speakers and the same boundaries. That is a speed result. No accuracy figure is claimed
for it, and none should be read into it.

## What is inside, and what still has to be downloaded

Speech detection ships **inside** both installers, 2.2 MiB of it, so that option works the moment
you first open the application.

Three things are downloads from the **Models** tab instead:

- **Speech recognition**, 1.34 GiB, and **English translation**, 1.34 GiB. Either one alone would
  put a release asset past GitHub's 2 GiB limit, which is the whole reason they are not bundled.
- **Speaker labelling**, 31 MiB, which additionally needs a free Hugging Face account and an
  accepted user agreement, because the weights are gated. That option stays inactive until you
  fetch it.

Everything you download lives in `%LOCALAPPDATA%\Uindosill`, outside the application folder, so
updating and reinstalling never touch it. Uninstalling asks you first, naming the size and the
folder, and keeps the files unless you explicitly answer Yes.

## Files you should ignore

`UindosillDesktop-*.nupkg` and `releases.*.json` are the update feed. The application fetches them
for you when it updates itself. There is nothing useful to do with them by hand.

---
