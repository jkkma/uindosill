# Keeping v3 dictation possible

Do not build these. Do not preclude them. Each is a known trap, and each is the reason dictation sits
behind both file transcription and asking questions about a transcript: the entire Win32 risk
surface lives on the dictation path and none of it on the other two, while the inference engine is
identical for all of them.

## What is already in place

- **`IAudioSource` serves live capture.** `Duration` is nullable precisely so an open-ended
  microphone stream is a legal source rather than a special case.
- **`SegmentingTranscriptionEngine` already yields per-utterance.** Dictation is the same shape with
  one segment at a time.
- **parakeet.cpp ABI v5 has cache-aware streaming with `<EOU>`/`<EOB>` events**, exposed as
  `parakeet_capi_stream_feed_json` / `parakeet_capi_stream_finalize_json` and typed event records.
  The bindings for those are not written, but the ABI is version-checked and the handle pattern is
  established, so adding them is mechanical.
- **`AudioMath.IsDigitalSilence`** already exists and already has a caller that reports it rather
  than returning an empty transcript.

## The traps

### Global hotkeys

Avalonia has none. `SharpHook`'s `SuppressEvent` **silently does nothing** on `TaskPoolGlobalHook` —
use `SimpleGlobalHook`, suppress the key-*up* as well as the key-down, and keep handlers trivially
cheap or you induce system-wide input lag. Expect antivirus heuristics to notice a global keyboard
hook; that is another reason it is not in v1.

### Text injection

`SendInput` **fails silently under UIPI**: Microsoft's own documentation states that neither the
return value nor `GetLastError` indicates the block. Clipboard + Ctrl+V is the only broadly reliable
method. Save and restore the clipboard **including on the cancel path**, and never lose the text on
failure — keep the last N transcripts recoverable. A dictation tool that eats a paragraph because the
target window refused the injection has done worse than nothing.

### No-focus-steal overlay

`WS_EX_NOACTIVATE` is unreliable in Avalonia
([#17097](https://github.com/AvaloniaUI/Avalonia/issues/17097), open, labelled both `bug` and
`by-design`). Avalonia *does* do frameless translucent windows in-process via
`WindowDecorations="None"` plus `TransparencyLevelHint="Transparent"`, which WPF currently cannot
([dotnet/wpf#11321](https://github.com/dotnet/wpf/issues/11321)) — so the overlay is achievable, the
focus behaviour is the open part.

Note `WindowDecorations`, not `SystemDecorations`: the latter is obsolete in Avalonia 12.

### Capture before the key settles

Start capturing on key-down, not after the hotkey is confirmed, or you lose the first 0.5–3 seconds
of the utterance. This is the same reasoning as the segmenter's pre-roll buffer, which already keeps
240 ms of audio before a detected onset for exactly this reason.

### Detect digital silence and say so

Returning an empty transcript when the microphone was muted is indistinguishable from a broken
install. `SegmentationReport` already distinguishes "digitally silent" from "audio present, no speech
detected", and both the CLI and the UI already surface the difference. Reuse it.

## What would need building

1. A `MicrophoneAudioSource : IAudioSource` (NAudio WASAPI capture, runtime-guarded the same way
   `MediaFoundationAudioSource` is).
2. Streaming bindings for `parakeet_capi_stream_*`, plus a `ParakeetStreamHandle : SafeHandle`
   alongside the existing context handle.
3. A `StreamingTranscriptionEngine` that does not go through `StreamingSegmenter` — the streaming
   model does its own chunking and carries encoder/decoder caches across feeds.
4. The Win32 surface above, each piece behind an interface in `Parakeet.Core` so the rest of the app
   never references it directly.

Nothing in v1 blocks any of that. That was the point of doing v1 first.
