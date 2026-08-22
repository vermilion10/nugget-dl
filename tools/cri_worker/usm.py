"""
USM demuxing.
SFA-track HCA decode and language detection, tracks() returns every
stream's raw bytes in memory.
"""

import io
import logging
from typing import Any, BinaryIO, Union, cast

from cri import HCA, USM

logger = logging.getLogger("cri_worker.usm")


class GameUsmDemuxer(USM):
    # Maps stream key (e.g. "@SFA_0") -> RFC 5646 language code.
    audio_language: dict[str, str]

    def __init__(
        self, filename: Union[str, bytes, BinaryIO], key: Union[str, int, bool] = False
    ):
        self.key = key
        self.audio_language = {}
        # Ex's USM only accepts a path or a file-like, not raw bytes.
        if isinstance(filename, (bytes, bytearray)):
            filename = io.BytesIO(filename)
        # Ex demuxes during construction. the game's SFA chunks are HCA (left
        # unmasked by Ex's reader); decode them once demux has populated output.
        super().__init__(filename, cast(str, key))
        self._decode_sfa_audio()

    def _decode_sfa_audio(self) -> None:
        filenames = self.CRIDObj.table.get("filename", [])
        i = 1
        for k, v in self.output.items():
            if k.startswith("@SFA_"):
                self.output[k] = HCA(cast(Any, v), key=cast(Any, self.key)).decode()
                if i < len(filenames):
                    self.audio_language[k] = ffmpeg_language_code(filenames[i])
            i += 1

    def tracks(self) -> list[dict]:
        """Every demuxed stream, video/audio/subtitle-skipped, as
        {"name", "kind", "language", "data"} dicts ready to hand back over
        the wire. Subtitles aren't supported (matches extract_video)."""
        result: list[dict] = []
        for k, v in self.output.items():
            if k.startswith("@SBT_"):
                continue
            if k.startswith("@SFA_"):
                result.append(
                    {
                        "name": k,
                        "kind": "audio",
                        "language": self.audio_language.get(k),
                        "data": v,
                    }
                )
            elif k.startswith("@SFV_"):
                result.append({"name": k, "kind": "video", "language": None, "data": v})
            else:
                logger.warning("Unknown stream: %s", k)
        return result


def ffmpeg_language_code(text: str) -> str:
    """
    Detect language tag inside a string and return the
    correct RFC 5646 language code.
    """

    text_lower = text.lower()

    mapping = {
        "jp": "ja",  # Japanese
        "en": "en",  # English
        "cn": "zh",  # Chinese (Simplified/Mandarin)
        "ct": "yue",  # Cantonese (Traditional Chinese audio)
    }

    for key, code in mapping.items():
        if key in text_lower:
            return code

    # close enough usually?
    return text_lower
