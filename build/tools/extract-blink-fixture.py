"""Extract a cropped BGR24 regression fixture from a reference blink video.

Usage:
    python build/tools/extract-blink-fixture.py <video> <output-prefix> --crop X Y W H

The video is decoded with the Windows Media Foundation backend (cv2.CAP_MSMF) because the
OpenCV FFmpeg backend shipped with opencv-python cannot decode AV1. The output is
<output-prefix>.bgr24.gz (raw packed BGR24 frames, no padding) and a JSON sidecar that must be
completed manually with the verified closed-eye frame ranges.
"""
import argparse
import gzip
import hashlib
import json
import os

import cv2


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("video")
    parser.add_argument("output_prefix")
    parser.add_argument("--crop", nargs=4, type=int, required=True, metavar=("X", "Y", "W", "H"))
    args = parser.parse_args()

    x, y, w, h = args.crop
    capture = cv2.VideoCapture(args.video, cv2.CAP_MSMF)
    if not capture.isOpened():
        raise SystemExit("failed to open video with MSMF backend")

    fps = capture.get(cv2.CAP_PROP_FPS)
    source_width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
    source_height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
    data = bytearray()
    count = 0
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        data += frame[y:y + h, x:x + w].tobytes()
        count += 1

    with gzip.open(args.output_prefix + ".bgr24.gz", "wb", compresslevel=9) as output:
        output.write(bytes(data))

    metadata = {
        "source": os.path.basename(args.video),
        "decoder": "opencv CAP_MSMF",
        "sourceSize": {"width": source_width, "height": source_height},
        "crop": {"x": x, "y": y, "width": w, "height": h},
        "pixelFormat": "Bgr24",
        "stride": w * 3,
        "fps": fps,
        "frameCount": count,
        "sha256": hashlib.sha256(bytes(data)).hexdigest(),
        "frameIndexBase": 0,
        "closedFrames": [],
    }
    with open(args.output_prefix + ".json", "w", encoding="utf-8") as output:
        json.dump(metadata, output, indent=2)
    print(f"frames={count} fps={fps}")


if __name__ == "__main__":
    main()
