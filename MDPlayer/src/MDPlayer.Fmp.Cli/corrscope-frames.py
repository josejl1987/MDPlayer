#!/usr/bin/env python3
"""mdplayer-render single-pass Corrscope bridge.

Renders a Corrscope YAML project to raw RGB0 frames on stdout (no video
encoding). mdplayer-render consumes the frame stream, composites the musical
overlay in memory, and hands the composited frames to a single FFmpeg encode.

Because Corrscope's CLI always resolves `--render` to a file, stdout output is
only reachable through its Python API. This script drives that API directly:

  CorrScope(cfg, Arguments(outputs=[RawFramesOutputConfig()])).play()

RawFramesOutputConfig subclasses FFmpegOutputConfig so CorrScope takes the
recording path (before_record), which forces every frame to be rendered
(render_subfps = 1). RawFramesOutput writes the raw frame bytes to stdout
instead of spawning FFmpeg, so no intermediate video is ever encoded.

Usage:
  corrscope-frames.py <project.yaml>   (raw RGB0 frames to stdout)
"""

import os
import sys
from contextlib import redirect_stdout
from pathlib import Path

from corrscope.config import yaml
from corrscope.corrscope import CorrScope, Arguments, pushd
from corrscope.outputs import FFmpegOutputConfig, Output, Stop
from corrscope.settings.global_prefs import Parallelism


_RAW_STDOUT = sys.stdout.buffer


class RawFramesOutputConfig(FFmpegOutputConfig):
    cls = None  # assigned after RawFramesOutput is defined


class RawFramesOutput(Output):
    """Writes raw frame bytes to stdout with no encoding step.

    The consumer (mdplayer-render) reads exactly `width*height*4` bytes per
    frame from the pipe. A frame whose byte count differs silently misaligns
    every later read, so each frame is validated against the config-declared
    size and the bridge fails fast instead of streaming corruption.
    """

    def __init__(self, corr_cfg, cfg):
        super().__init__(corr_cfg, cfg)
        # CorrScope's parallel renderer emits legacy diagnostics through
        # sys.stdout. Keep the original binary transport handle independent of
        # the temporary text-stream redirection around CorrScope.play().
        self._stream = _RAW_STDOUT
        self._closed = False
        self._expected = None

    def write_frame(self, frame):
        if frame is None:
            # Corrscope can report "no scope frame" for a tick (e.g. a frame
            # boundary that falls in a gap with nothing to draw). That is not a
            # corrupt stream: skip the tick instead of treating it as a malformed
            # frame, which previously raised len(None) and aborted the render.
            return None
        if self._expected is None:
            # Corrscope forces res_divisor to 1 before recording, so the
            # rendered frame is exactly width x height RGBA.
            render = self.corr_cfg.render
            width = render.divided_width
            height = render.divided_height
            self._expected = width * height * 4
            print(
                f"raw frame: {width}x{height}, rgba, packed stride {width * 4}, "
                f"{self._expected} bytes/frame",
                file=sys.stderr,
                flush=True,
            )
        actual = len(frame)
        if actual != self._expected:
            print(
                f"corrscope-frames error: frame contains {actual} bytes; "
                f"mdplayer-render expects exactly {self._expected}.",
                file=sys.stderr,
                flush=True,
            )
            return Stop
        try:
            self._stream.write(frame)
            return None
        except (BrokenPipeError, OSError):
            # The consumer closed the pipe (finished reading, or crashed).
            return Stop

    def close(self, wait=True):
        if not self._closed:
            self._closed = True
            try:
                self._stream.flush()
            except (BrokenPipeError, OSError):
                pass
        return 0

    def terminate(self, from_same_thread=True):
        self.close(wait=False)


RawFramesOutputConfig.cls = RawFramesOutput


def main():
    if len(sys.argv) != 2:
        print("usage: corrscope-frames.py <project.yaml>", file=sys.stderr)
        return 2

    yaml_path = Path(sys.argv[1]).resolve()
    cfg_dir = str(yaml_path.parent)

    try:
        with pushd(cfg_dir):
            cfg = yaml.load(yaml_path)
            render_cores = max(1, min(4, os.cpu_count() or 1))
            arg = Arguments(
                cfg_dir=cfg_dir,
                outputs=[RawFramesOutputConfig(path=None)],
                parallelism=Parallelism(
                    parallel=render_cores > 1,
                    max_render_cores=render_cores,
                ),
                # Corrscope's default progress callback prints to stdout,
                # which is the binary frame transport here. A no-op keeps the
                # transport clean and avoids per-progress flush work.
                progress=lambda _progress: None,
            )
            # Corrscope's parallel worker path has legacy progress/debug prints
            # on stdout. Keep those on stderr because stdout is raw frame data;
            # RawFramesOutput captured the original binary stream above.
            with redirect_stdout(sys.stderr):
                CorrScope(cfg, arg).play()
        # Flush explicitly so interpreter shutdown has nothing left to write —
        # the consumer may close the pipe immediately after the last frame.
        try:
            sys.stdout.flush()
        except (BrokenPipeError, OSError):
            pass
        return 0
    except BrokenPipeError:
        return 0
    except BaseException as exc:  # noqa: BLE001 — surface any corrscope error
        import traceback
        traceback.print_exc(file=sys.stderr)
        print(f"corrscope-frames error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
