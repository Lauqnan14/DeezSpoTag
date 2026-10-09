#!/usr/bin/env python3
"""
Vibe Analyzer CLI - lidify parity implementation.

This script ports lidify's Essentia analysis logic (standard + enhanced ML)
while preserving DeezSpoTag's CLI contract:
- --probe
- --file + --models
- JSON payload with PascalCase fields consumed by .NET.
"""

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import multiprocessing
from concurrent.futures import ProcessPoolExecutor, TimeoutError, as_completed
from typing import Any, Dict, List, Optional, Tuple

# Default to CPU-only TensorFlow/Essentia execution unless explicitly disabled.
# This avoids repeated CUDA probe failures on CPU-only hosts and reduces native
# runtime instability from GPU initialization attempts.
if os.environ.get("VIBE_ANALYZER_FORCE_CPU", "1").strip().lower() in {"1", "true", "yes", "on"}:
    os.environ.setdefault("CUDA_VISIBLE_DEVICES", "-1")
    os.environ.setdefault("TF_USE_CUDA", "0")
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

try:
    multiprocessing.set_start_method("spawn", force=True)
except RuntimeError:
    pass


# Essentia imports (graceful fallback)
ESSENTIA_AVAILABLE = False
es = None
EssentiaPool = None
try:
    import essentia  # type: ignore
    essentia.log.warningActive = False
    essentia.log.infoActive = False
    import essentia.standard as es  # type: ignore
    # essentia.Pool is the container used to pass named inputs into
    # TensorflowPredict (required by the MAEST -> Discogs519 head path).
    EssentiaPool = getattr(essentia, "Pool", None)
    ESSENTIA_AVAILABLE = True
except ImportError:
    pass

# Numpy import (graceful fallback for probe path)
np = None
try:
    import numpy as np  # type: ignore
except ImportError:
    np = None


# Required/optional algorithms (probe + runtime)
def _required(name: str):
    algo = getattr(es, name, None) if es is not None else None
    if algo is None:
        raise RuntimeError(f"essentia missing required algorithm: {name}")
    return algo


def _optional(name: str):
    return getattr(es, name, None) if es is not None else None


def probe_capabilities() -> Tuple[List[str], List[str]]:
    required = [
        "MonoLoader",
        "TensorflowPredictMusiCNN",
        "TensorflowPredict2D",
        "RhythmExtractor2013",
        "KeyExtractor",
        "Loudness",
        "DynamicComplexity",
        "Danceability",
        "Windowing",
        "Spectrum",
        "RMS",
        "Centroid",
        "FlatnessDB",
        "ZeroCrossingRate",
    ]
    optional = [
        "TensorflowPredictEffnetDiscogs",
        "TensorflowPredictMAEST",
    ]

    missing_required = [name for name in required if (es is None or getattr(es, name, None) is None)]
    missing_optional = [name for name in optional if (es is None or getattr(es, name, None) is None)]
    if np is None:
        missing_required.append("numpy")

    return missing_required, missing_optional


if ESSENTIA_AVAILABLE and np is not None:
    MonoLoader = _required("MonoLoader")
    TensorflowPredictMusiCNN = _required("TensorflowPredictMusiCNN")
    TensorflowPredict2D = _required("TensorflowPredict2D")

    RhythmExtractor2013 = _required("RhythmExtractor2013")
    KeyExtractor = _required("KeyExtractor")
    Loudness = _required("Loudness")
    DynamicComplexity = _required("DynamicComplexity")
    EssentiaDanceability = _required("Danceability")

    Windowing = _required("Windowing")
    Spectrum = _required("Spectrum")
    RMS = _required("RMS")
    Centroid = _required("Centroid")
    FlatnessDB = _required("FlatnessDB")
    ZeroCrossingRate = _required("ZeroCrossingRate")

    TensorflowPredictEffnetDiscogs = _optional("TensorflowPredictEffnetDiscogs")
    TensorflowPredictMAEST = _optional("TensorflowPredictMAEST")
    TensorflowPredict = _optional("TensorflowPredict")
else:
    MonoLoader = None
    TensorflowPredictMusiCNN = None
    TensorflowPredict2D = None
    RhythmExtractor2013 = None
    KeyExtractor = None
    Loudness = None
    DynamicComplexity = None
    EssentiaDanceability = None
    Windowing = None
    Spectrum = None
    RMS = None
    Centroid = None
    FlatnessDB = None
    ZeroCrossingRate = None
    TensorflowPredictEffnetDiscogs = None
    TensorflowPredictMAEST = None
    TensorflowPredict = None


REQUIRED_ENHANCED_MODELS = [
    "msd-musicnn-1.pb",
    "mood_happy-msd-musicnn-1.pb",
    "mood_sad-msd-musicnn-1.pb",
    "mood_relaxed-msd-musicnn-1.pb",
    "mood_aggressive-msd-musicnn-1.pb",
]

# Acoustic genre model manifests. Both branches are optional relative to
# enhanced mode; whichever one loads is reported honestly as GenreModel.
GENRE_MODEL_DISCogs519 = "discogs519-maest-30s-pw-519l"
GENRE_MODEL_DISCogs400 = "discogs400-discogs-effnet"

REQUIRED_GENRE519_MODELS = [
    "discogs-maest-30s-pw-519l-2.pb",
    "genre_discogs519-discogs-maest-30s-pw-519l-1.pb",
    "genre_discogs519-discogs-maest-30s-pw-519l-1.json",
]

REQUIRED_GENRE400_MODELS = [
    "discogs-effnet-bs64-1.pb",
    "genre_discogs400-discogs-effnet-1.pb",
    "genre_discogs400-discogs-effnet-1.json",
]

# Top-K genre evidence. The threshold is relative to the top-1 score so it stays
# meaningful across 400-way and 519-way softmax heads, instead of using a fixed
# absolute floor that a 519-way head almost never clears.
GENRE_EVIDENCE_TOP_K = 8
GENRE_SCORE_ABSOLUTE_FLOOR = 0.01
GENRE_SCORE_RELATIVE_RATIO = 0.25

FFMPEG_ENV_NAMES = ("DEEZSPOTAG_FFMPEG_PATH", "FFMPEG_PATH")

DEFAULT_MAX_ANALYSIS_SECONDS = 600
MIN_MAX_ANALYSIS_SECONDS = 30
MAX_MAX_ANALYSIS_SECONDS = 7200

MAX_ANALYSIS_SECONDS_ENV_NAME = "VIBE_ANALYZER_MAX_SECONDS"

# ---------------------------------------------------------------------------
# Sonic Analysis
#
# The primary Sonic representation is the Discogs-EffNet embedding. It is
# already part of the installed Essentia ecosystem and this analyzer already
# loads the extractor, so no new model family or ML runtime is introduced.
#
# The extractor emits one 1280-wide frame per second of audio, so a 600 s track
# yields ~600 frames. Frames are mean-pooled to a single track-level vector and
# L2-normalized, which makes cosine similarity a plain dot product.
#
# A pooled vector therefore depends on the analysed duration. Callers must treat
# the source audio length as part of the cache key, not only size and mtime.
# ---------------------------------------------------------------------------
SONIC_MODEL_ID = "discogs-effnet-bs64-1"
SONIC_MODEL_VERSION = "1"
SONIC_OUTPUT_NODE = "PartitionedCall:1"
SONIC_EMBEDDING_VERSION = "embedding-v1"
SONIC_POOLING_METHOD = "mean-v1"
SONIC_NORMALIZATION_METHOD = "l2-v1"
SONIC_DISTANCE_METRIC = "cosine"
SONIC_DIMENSIONS = 1280
SONIC_EMBEDDING_FILE = "discogs-effnet-bs64-1.pb"

SONIC_ENABLED_ENV_NAME = "VIBE_SONIC_ENABLED"


def sonic_enabled() -> bool:
    return os.environ.get(SONIC_ENABLED_ENV_NAME, "").strip().lower() in {"1", "true", "yes", "on"}


def pool_sonic_embedding(frames: Any) -> Optional[Dict[str, Any]]:
    """Mean-pool frame embeddings into one normalized track vector.

    Kept as a single testable entry point so the pooling contract cannot drift
    between the worker and batch paths. Returns None when the input is unusable
    rather than emitting a vector with NaN or Infinity in it, because a corrupt
    vector silently poisons every similarity computed from it.
    """
    if frames is None or np is None:
        return None

    try:
        matrix = np.asarray(frames, dtype=np.float64)
    except Exception:
        return None

    if matrix.ndim == 1:
        matrix = matrix.reshape(1, -1)
    if matrix.ndim != 2 or matrix.shape[0] == 0:
        return None

    pooled = matrix.mean(axis=0)
    if not np.isfinite(pooled).all():
        return None

    norm = float(np.linalg.norm(pooled))
    if not np.isfinite(norm) or norm <= 0.0:
        return None

    normalized = pooled / norm
    if not np.isfinite(normalized).all():
        return None

    return {
        "modelId": SONIC_MODEL_ID,
        "modelVersion": SONIC_MODEL_VERSION,
        "embeddingVersion": SONIC_EMBEDDING_VERSION,
        "dimensions": int(normalized.shape[0]),
        "pooling": SONIC_POOLING_METHOD,
        "normalization": SONIC_NORMALIZATION_METHOD,
        "distanceMetric": SONIC_DISTANCE_METRIC,
        "frameCount": int(matrix.shape[0]),
        "values": normalized.tolist(),
    }

_warned_once: set = set()


def _warn_once(key: str, message: str) -> None:
    """Emit a single stderr diagnostic per process so a per-track failure does
    not flood the worker's diagnostics buffer."""
    if key in _warned_once:
        return
    _warned_once.add(key)
    try:
        sys.stderr.write(message.rstrip() + "\n")
        sys.stderr.flush()
    except Exception:
        pass


def resolve_max_analysis_seconds() -> int:
    """Upper bound on how much audio is decoded per track. Long files (DJ mixes,
    audiobooks) otherwise produce multi-hundred-megabyte temp WAVs and blow the
    per-track request timeout, which restarts the analyzer worker."""
    raw = os.environ.get(MAX_ANALYSIS_SECONDS_ENV_NAME, "").strip()
    try:
        configured = int(raw) if raw else DEFAULT_MAX_ANALYSIS_SECONDS
    except ValueError:
        configured = DEFAULT_MAX_ANALYSIS_SECONDS
    return max(MIN_MAX_ANALYSIS_SECONDS, min(MAX_MAX_ANALYSIS_SECONDS, configured))


def top_genre_evidence(
    scores: List[float],
    labels: List[str],
    model: str,
) -> List[Dict[str, Any]]:
    """Pick the top-K genre labels using a scale-relative threshold.

    Kept free of numpy so the rule is directly unit-testable, and shared by the
    Discogs519/MAEST and Discogs400/EffNet branches so both behave identically.
    """
    if not scores:
        return []

    if len(scores) != len(labels) or any(not isinstance(label, str) or not label.strip() for label in labels):
        raise ValueError("Genre scores must align with nonempty class labels")

    order = sorted(range(len(scores)), key=lambda index: scores[index], reverse=True)
    top_score = scores[order[0]]
    if top_score <= 0:
        return []

    threshold = max(GENRE_SCORE_ABSOLUTE_FLOOR, top_score * GENRE_SCORE_RELATIVE_RATIO)

    evidence: List[Dict[str, Any]] = []
    for index in order[:GENRE_EVIDENCE_TOP_K]:
        score = scores[index]
        # The top-1 label is always kept so a loaded model never yields empty
        # evidence, however diffuse the head's distribution is.
        if score < threshold and evidence:
            continue
        label = labels[index]
        evidence.append({
            "label": label,
            "score": round(float(score), 4),
            "model": model,
        })
        if len(evidence) >= GENRE_EVIDENCE_TOP_K:
            break
    return evidence


def _mean_scores(scores: Any, expected_class_count: int) -> List[float]:
    """Average frame/batch dimensions while preserving the final class axis."""
    if scores is None or scores.size == 0:
        return []
    if expected_class_count <= 0 or scores.ndim == 0 or scores.shape[-1] != expected_class_count:
        raise ValueError(
            f"Expected {expected_class_count} genre classes on the final axis; got {scores.shape}")
    mean_scores = scores.reshape(-1, expected_class_count).mean(axis=0)
    return [float(value) for value in mean_scores]


def resolve_ffmpeg_path() -> Optional[str]:
    for env_name in FFMPEG_ENV_NAMES:
        configured = os.environ.get(env_name)
        if configured and os.path.isfile(configured):
            return configured
    return shutil.which("ffmpeg")


class AudioAnalyzer:
    """Lidify audio analysis core (ported)."""

    def __init__(self, models_dir: str):
        self.models_dir = models_dir
        self.enhanced_mode = False
        self.musicnn_model = None
        self.prediction_models: Dict[str, Any] = {}
        self.effnet_extractor = None
        self.maest_genre_extractor = None
        self.genre519_predictor = None
        self.genre519_labels = []
        self.genre_model_name = None
        self.deam_predictor = self._load_deam_model()
        self.genre_predictor = None
        self.genre_labels: List[str] = []
        self.sonic_enabled = sonic_enabled()
        self.sonic_extractor = None

        self.rhythm_extractor = None
        self.key_extractor = None
        self.loudness = None
        self.dynamic_complexity = None
        self.danceability_extractor = None
        self.spectral_centroid = None
        self.spectral_flatness = None
        self.zcr = None
        self.rms = None
        self.spectrum = None
        self.windowing = None

        if ESSENTIA_AVAILABLE and np is not None:
            self._init_essentia()
            self._load_ml_models()

    def _init_essentia(self):
        self.rhythm_extractor = RhythmExtractor2013(method="multifeature")
        self.key_extractor = KeyExtractor()
        self.loudness = Loudness()
        self.dynamic_complexity = DynamicComplexity()
        self.danceability_extractor = EssentiaDanceability()
        self.spectral_centroid = Centroid(range=22050)
        self.spectral_flatness = FlatnessDB()
        self.zcr = ZeroCrossingRate()
        self.rms = RMS()
        self.spectrum = Spectrum()
        self.windowing = Windowing(type="hann")

    def _model_path(self, file_name: str) -> str:
        return os.path.join(self.models_dir, file_name)

    def _load_genre_labels(self) -> List[str]:
        meta_path = self._model_path("genre_discogs400-discogs-effnet-1.json")
        if not os.path.exists(meta_path):
            return []

        try:
            with open(meta_path, "r", encoding="utf-8") as handle:
                payload = json.load(handle)
            labels = payload.get("classes") if isinstance(payload, dict) else None
            if not isinstance(labels, list):
                return []
            return [str(label) for label in labels if isinstance(label, str) and label.strip()]
        except Exception:
            return []

    def _extract_essentia_genre_evidence(self, audio_16k):
        if (
            self.maest_genre_extractor is not None
            and self.genre519_predictor is not None
            and np is not None
            and EssentiaPool is not None
        ):
            try:
                embeddings = self.maest_genre_extractor(audio_16k)
                pool = EssentiaPool()
                pool.set("embeddings", embeddings)
                scores = np.array(self.genre519_predictor(pool)["PartitionedCall/Identity_1"])
                return top_genre_evidence(
                    _mean_scores(scores, len(self.genre519_labels)),
                    self.genre519_labels,
                    self.genre_model_name or GENRE_MODEL_DISCogs519,
                )
            except Exception as exc:
                # Degrade to the EffNet/Discogs400 branch instead of returning no
                # evidence at all, and say so once so the cause is diagnosable.
                _warn_once(
                    "discogs519",
                    f"vibe analyzer Discogs519/MAEST genre extraction failed, "
                    f"falling back to Discogs400/EffNet: {exc}",
                )

        if self.effnet_extractor is None or self.genre_predictor is None or np is None:
            return []

        try:
            effnet_embeddings = self.effnet_extractor(audio_16k)
            scores = np.array(self.genre_predictor(effnet_embeddings))
            return top_genre_evidence(
                _mean_scores(scores, len(self.genre_labels)),
                self.genre_labels,
                GENRE_MODEL_DISCogs400,
            )
        except Exception as exc:
            _warn_once("discogs400", f"vibe analyzer Discogs400/EffNet genre extraction failed: {exc}")
            return []


    def _load_deam_model(self):
        path = self._model_path("deam-msd-musicnn-2.pb")
        if not os.path.exists(path) or TensorflowPredict2D is None:
            return None
        try:
            return TensorflowPredict2D(
                graphFilename=path,
                input="serving_default_model_Placeholder",
                output="model/Identity",
            )
        except Exception:
            return None

    def _predict_deam(self, audio_16k):
        if self.deam_predictor is None or np is None:
            return None
        try:
            predictions = np.array(self.deam_predictor(audio_16k), dtype=float)
            frame_scores = predictions.mean(axis=0) if predictions.ndim == 2 else predictions.reshape(-1)
            if frame_scores.size < 2:
                return None
            valence = float(np.clip((frame_scores[0] - 1.0) / 8.0, 0.0, 1.0))
            arousal = float(np.clip((frame_scores[1] - 1.0) / 8.0, 0.0, 1.0))
            return {
                "valence": round(valence, 3),
                "arousal": round(arousal, 3),
                "valenceSource": "deam-msd-musicnn-2",
                "arousalSource": "deam-msd-musicnn-2",
            }
        except Exception:
            return None

    def _create_prediction_head(self, file_name: str):
        model_path = self._model_path(file_name)
        if not os.path.exists(model_path):
            return None
        try:
            return TensorflowPredict2D(
                graphFilename=model_path,
                output="model/Softmax",
            )
        except Exception:
            return None

    def _load_prediction_heads(self, heads_to_load: Dict[str, str]) -> None:
        for model_name, file_name in heads_to_load.items():
            predictor = self._create_prediction_head(file_name)
            if predictor is not None:
                self.prediction_models[model_name] = predictor

    def _load_sonic_model(self) -> None:
        """Load the EffNet extractor dedicated to Sonic Analysis.

        The genre branch only reaches EffNet when MAEST is unavailable, so a
        dedicated instance is required: otherwise Sonic Analysis would silently
        produce no vectors on any deployment that ships MAEST, which is every
        application image. Load failures are non-fatal; semantic analysis must
        still complete when Sonic cannot run.
        """
        if not self.sonic_enabled or np is None:
            return

        if TensorflowPredictEffnetDiscogs is None:
            _warn_once(
                "sonic-algorithm",
                "vibe analyzer cannot use Sonic Analysis: Essentia is missing "
                "TensorflowPredictEffnetDiscogs.",
            )
            return

        model_path = self._model_path(SONIC_EMBEDDING_FILE)
        if not os.path.exists(model_path) or os.path.getsize(model_path) <= 0:
            _warn_once(
                "sonic-model",
                "vibe analyzer cannot use Sonic Analysis; missing model file: "
                + os.path.basename(model_path),
            )
            return

        try:
            self.sonic_extractor = TensorflowPredictEffnetDiscogs(
                graphFilename=model_path,
                output=SONIC_OUTPUT_NODE,
            )
        except Exception as exc:
            self.sonic_extractor = None
            _warn_once(
                "sonic-init",
                f"vibe analyzer failed to initialize Sonic Analysis: {exc}",
            )

    def _extract_sonic_embedding(self, audio_16k) -> Optional[Dict[str, Any]]:
        """Run the Sonic extractor and pool its frames into one track vector."""
        if self.sonic_extractor is None:
            return None
        try:
            return pool_sonic_embedding(self.sonic_extractor(audio_16k))
        except Exception as exc:
            _warn_once(
                "sonic-extract",
                f"vibe analyzer Sonic embedding extraction failed: {exc}",
            )
            return None

    def _load_effnet_genre_models(self) -> None:
        if TensorflowPredictEffnetDiscogs is None:
            return

        effnet_model_path = self._model_path("discogs-effnet-bs64-1.pb")
        genre_model_path = self._model_path("genre_discogs400-discogs-effnet-1.pb")
        if os.path.exists(effnet_model_path):
            try:
                self.effnet_extractor = TensorflowPredictEffnetDiscogs(
                    graphFilename=effnet_model_path,
                    output="PartitionedCall:1",
                )
            except Exception:
                self.effnet_extractor = None

        if self.effnet_extractor is not None and os.path.exists(genre_model_path):
            try:
                self.genre_predictor = TensorflowPredict2D(
                    graphFilename=genre_model_path,
                    input="serving_default_model_Placeholder",
                    output="PartitionedCall",
                )
            except Exception:
                self.genre_predictor = None

        if self.genre_predictor is not None:
            self.genre_labels = self._load_genre_labels()
            if self.genre_labels and self.genre_model_name is None:
                # Only claim this model when the head, the extractor and the label
                # set are all usable; otherwise GenreModel stays unset and the
                # payload reports the truth (no genre model available).
                self.genre_model_name = GENRE_MODEL_DISCogs400

    def _load_discogs519_models(self):
        genre_model = os.environ.get("VIBE_GENRE_MODEL", "discogs519").strip().lower()
        if genre_model != "discogs519":
            # Explicit diagnostic override. Provenance is still decided by whether
            # the Discogs400 head actually loads.
            return

        if TensorflowPredictMAEST is None or TensorflowPredict is None or EssentiaPool is None:
            _warn_once(
                "discogs519-algorithms",
                "vibe analyzer cannot use Discogs519/MAEST: Essentia is missing "
                "TensorflowPredictMAEST, TensorflowPredict or essentia.Pool.",
            )
            return

        maest_path = self._model_path("discogs-maest-30s-pw-519l-2.pb")
        genre519_path = self._model_path("genre_discogs519-discogs-maest-30s-pw-519l-1.pb")
        labels_path = self._model_path("genre_discogs519-discogs-maest-30s-pw-519l-1.json")
        missing = [
            path
            for path in (maest_path, genre519_path, labels_path)
            if not os.path.exists(path) or os.path.getsize(path) <= 0
        ]
        if missing:
            _warn_once(
                "discogs519-models",
                "vibe analyzer cannot use Discogs519/MAEST; missing model files: "
                + ", ".join(os.path.basename(path) for path in missing),
            )
            return

        try:
            with open(labels_path, "r", encoding="utf-8") as handle:
                payload = json.load(handle)
            labels = payload.get("classes") if isinstance(payload, dict) else None
            self.genre519_labels = [str(label) for label in labels or []]
            if len(self.genre519_labels) != 519:
                raise RuntimeError(
                    f"Discogs519 expected 519 labels; found {len(self.genre519_labels)}")

            self.maest_genre_extractor = TensorflowPredictMAEST(
                graphFilename=maest_path,
                output="PartitionedCall/Identity_12",
            )
            self.genre519_predictor = TensorflowPredict(
                graphFilename=genre519_path,
                inputs=["embeddings"],
                outputs=["PartitionedCall/Identity_1"],
            )
            self.genre_model_name = GENRE_MODEL_DISCogs519
        except Exception as exc:
            self.maest_genre_extractor = None
            self.genre519_predictor = None
            self.genre519_labels = []
            _warn_once(
                "discogs519-load",
                f"vibe analyzer failed to initialize Discogs519/MAEST: {exc}",
            )

    def _load_ml_models(self):
        self._load_discogs519_models()
        self._load_sonic_model()
        if TensorflowPredictMusiCNN is None or TensorflowPredict2D is None:
            self.enhanced_mode = False
            return

        try:
            base_model = self._model_path("msd-musicnn-1.pb")
            if not os.path.exists(base_model):
                self.enhanced_mode = False
                return

            self.musicnn_model = TensorflowPredictMusiCNN(
                graphFilename=base_model,
                output="model/dense/BiasAdd",
            )

            self._load_prediction_heads(
                {
                    "mood_happy": "mood_happy-msd-musicnn-1.pb",
                    "mood_sad": "mood_sad-msd-musicnn-1.pb",
                    "mood_relaxed": "mood_relaxed-msd-musicnn-1.pb",
                    "mood_aggressive": "mood_aggressive-msd-musicnn-1.pb",
                    "mood_party": "mood_party-msd-musicnn-1.pb",
                    "mood_acoustic": "mood_acoustic-msd-musicnn-1.pb",
                    "mood_electronic": "mood_electronic-msd-musicnn-1.pb",
                    "danceability": "danceability-msd-musicnn-1.pb",
                    "voice_instrumental": "voice_instrumental-msd-musicnn-1.pb",
                }
            )

            required = ["mood_happy", "mood_sad", "mood_relaxed", "mood_aggressive"]
            self.enhanced_mode = all(key in self.prediction_models for key in required)
            self._load_effnet_genre_models()
        except Exception:
            self.enhanced_mode = False

    def load_audio(self, file_path: str, sample_rate: int) -> Optional[Any]:
        if MonoLoader is None:
            return None
        try:
            loader = MonoLoader(filename=file_path, sampleRate=sample_rate)
            return loader()
        except Exception:
            return None

    @staticmethod
    def _discard_temp_file(temp_path: str) -> None:
        try:
            os.remove(temp_path)
        except OSError:
            pass

    def _transcode_to_temp_wav(self, file_path: str) -> Tuple[Optional[str], Optional[str]]:
        ffmpeg_path = resolve_ffmpeg_path()
        if not ffmpeg_path:
            return (None, "ffmpeg is not available for audio decode fallback")

        max_seconds = resolve_max_analysis_seconds()
        temp_file = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
        temp_path = temp_file.name
        temp_file.close()

        command = [
            ffmpeg_path,
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            file_path,
            "-map",
            "0:a:0",
            # Bound the decode so very long files cannot produce a multi-hundred
            # megabyte WAV or exceed the host per-track request timeout.
            "-t",
            str(max_seconds),
            "-ac",
            "1",
            "-ar",
            "44100",
            "-vn",
            "-f",
            "wav",
            temp_path,
        ]
        try:
            completed = subprocess.run(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=120,
            )
            if completed.returncode != 0 or not os.path.exists(temp_path) or os.path.getsize(temp_path) == 0:
                message = (completed.stderr or "ffmpeg could not decode audio").strip()
                # ffmpeg has already created the file; leaving a partial/empty WAV
                # behind leaks container storage on every undecodable track.
                self._discard_temp_file(temp_path)
                return (None, message)
            return (temp_path, None)
        except subprocess.TimeoutExpired:
            self._discard_temp_file(temp_path)
            return (None, "ffmpeg decode fallback timed out")
        except Exception as exc:
            self._discard_temp_file(temp_path)
            return (None, str(exc))

    def load_audio_pair(self, file_path: str) -> Tuple[Optional[Any], Optional[Any], Optional[str]]:
        temp_path, error = self._transcode_to_temp_wav(file_path)
        if temp_path is None:
            return (None, None, error or "Unable to decode audio")

        try:
            audio_44k = self.load_audio(temp_path, 44100)
            audio_16k = self.load_audio(temp_path, 16000)
            if audio_44k is None or audio_16k is None:
                return (None, None, "Unable to decode transcoded audio")
            return (audio_44k, audio_16k, None)
        finally:
            self._discard_temp_file(temp_path)

    @staticmethod
    def _default_analysis_result() -> Dict[str, Any]:
        return {
            "bpm": None,
            "beatsCount": None,
            "key": None,
            "keyScale": None,
            "keyStrength": None,
            "energy": None,
            "loudness": None,
            "dynamicRange": None,
            "danceability": None,
            "valence": None,
            "arousal": None,
            "valenceSource": "heuristic-fallback",
            "arousalSource": "heuristic-fallback",
            "instrumentalness": None,
            "acousticness": None,
            "speechiness": None,
            "moodTags": [],
            "essentiaGenres": [],
            "moodHappy": None,
            "moodSad": None,
            "moodRelaxed": None,
            "moodAggressive": None,
            "moodParty": None,
            "moodAcoustic": None,
            "moodElectronic": None,
            "danceabilityMl": None,
            "analysisMode": "standard",
        }

    def _collect_frame_statistics(self, audio_44k) -> Dict[str, List[float]]:
        stats: Dict[str, List[float]] = {
            "rms": [],
            "zcr": [],
            "spectral_centroid": [],
            "spectral_flatness": [],
        }
        frame_size = 2048
        hop_size = 1024
        for i in range(0, len(audio_44k) - frame_size, hop_size):
            frame = audio_44k[i:i + frame_size]
            windowed = self.windowing(frame)
            spectrum = self.spectrum(windowed)
            stats["rms"].append(float(self.rms(frame)))
            stats["zcr"].append(float(self.zcr(frame)))
            stats["spectral_centroid"].append(float(self.spectral_centroid(spectrum)))
            stats["spectral_flatness"].append(float(self.spectral_flatness(spectrum)))
        return stats

    def _extract_core_audio_metrics(self, audio_44k) -> Dict[str, Any]:
        bpm, beats, _, _, _ = self.rhythm_extractor(audio_44k)
        key, scale, strength = self.key_extractor(audio_44k)
        stats = self._collect_frame_statistics(audio_44k)
        rms_values = stats["rms"]
        danceability, _ = self.danceability_extractor(audio_44k)
        dynamic_range, _ = self.dynamic_complexity(audio_44k)
        return {
            "bpm": round(float(bpm), 1),
            "beatsCount": int(len(beats)) if beats is not None else None,
            "key": key,
            "keyScale": scale,
            "keyStrength": round(float(strength), 3),
            "energy": round(min(1.0, float(np.mean(rms_values)) * 3), 3) if rms_values else 0.5,
            "loudness": round(float(self.loudness(audio_44k)), 2),
            "dynamicRange": round(float(dynamic_range), 2),
            "_spectral_centroid": float(np.mean(stats["spectral_centroid"])) if stats["spectral_centroid"] else 0.5,
            "_spectral_flatness": float(np.mean(stats["spectral_flatness"])) if stats["spectral_flatness"] else -20.0,
            "_zcr": float(np.mean(stats["zcr"])) if stats["zcr"] else 0.1,
            "danceability": round(max(0.0, min(1.0, float(danceability))), 3),
        }

    def analyze(self, file_path: str) -> Dict[str, Any]:
        result = self._default_analysis_result()

        if not ESSENTIA_AVAILABLE or np is None:
            result["_error"] = "Essentia library not installed"
            return result

        audio_44k, audio_16k, decode_error = self.load_audio_pair(file_path)
        if audio_44k is None or audio_16k is None:
            result["_error"] = decode_error or "Unable to decode audio"
            return result

        # A decoded buffer that fills the configured budget means the source was
        # longer than the cap; surface it instead of silently analysing a prefix.
        max_seconds = resolve_max_analysis_seconds()
        result["audioTruncated"] = len(audio_44k) >= int(44100 * max_seconds * 0.99)

        try:
            result.update(self._extract_core_audio_metrics(audio_44k))
            bpm = result.get("bpm")
            scale = result.get("keyScale")

            if self.enhanced_mode:
                try:
                    ml_features = self._extract_ml_features(audio_16k)
                    result.update(ml_features)
                    result["analysisMode"] = "enhanced"
                except Exception as exc:
                    _warn_once("ml-features", f"vibe analyzer ML feature extraction failed: {exc}")
                    self._apply_standard_estimates(result, scale, bpm)
            else:
                self._apply_standard_estimates(result, scale, bpm)

            genre_evidence = self._extract_essentia_genre_evidence(audio_16k)

            result["essentiaGenreEvidence"] = genre_evidence

            # Report only the model that actually produced the evidence above.
            result["genreModel"] = self.genre_model_name

            result["essentiaGenres"] = [entry["label"] for entry in genre_evidence]
            result["moodTags"] = self._generate_mood_tags(result)

            # Sonic Analysis is additive and strictly isolated: a failure here
            # must never turn a completed semantic analysis into a failure. It
            # runs outside the block that can raise, and a missing vector simply
            # reports "sonicUnavailable" so the caller can record that state
            # without failing the track.
            if self.sonic_enabled:
                try:
                    sonic = self._extract_sonic_embedding(audio_16k)
                    if sonic is None:
                        result["sonicUnavailable"] = True
                    else:
                        result["sonicEmbedding"] = sonic
                except Exception as exc:
                    _warn_once(
                        "sonic-analyze",
                        f"vibe analyzer Sonic analysis failed: {exc}",
                    )
                    result["sonicUnavailable"] = True
        except Exception as exc:
            result["_error"] = str(exc)

        for field in ["_spectral_centroid", "_spectral_flatness", "_zcr"]:
            result.pop(field, None)

        return result

    def _safe_predict(self, model, embeddings) -> Tuple[float, float]:
        try:
            preds = np.array(model(embeddings))
            if preds.ndim == 2 and preds.shape[1] > 1:
                positive_probs = preds[:, 1]
            else:
                positive_probs = preds.reshape(-1)
            raw_value = float(np.mean(positive_probs))
            variance = float(np.var(positive_probs))
            return (round(max(0.0, min(1.0, raw_value)), 3), round(variance, 4))
        except Exception:
            return (0.5, 0.0)

    def _collect_raw_moods(self, embeddings, mood_map: Dict[str, str]) -> Dict[str, Tuple[float, float]]:
        raw_moods: Dict[str, Tuple[float, float]] = {}
        for model_key, output_key in mood_map.items():
            predictor = self.prediction_models.get(model_key)
            if predictor is None:
                continue
            raw_moods[output_key] = self._safe_predict(predictor, embeddings)
        return raw_moods

    @staticmethod
    def _normalize_core_moods(raw_moods: Dict[str, Tuple[float, float]]) -> None:
        core_moods = ["moodHappy", "moodSad", "moodRelaxed", "moodAggressive"]
        core_values = [raw_moods[m][0] for m in core_moods if m in raw_moods]
        if len(core_values) < 4:
            return

        min_mood = min(core_values)
        max_mood = max(core_values)
        if not (min_mood > 0.7 and (max_mood - min_mood) < 0.3):
            return

        for mood_key in core_moods:
            if mood_key not in raw_moods:
                continue
            old_val, var = raw_moods[mood_key]
            if max_mood > min_mood:
                normalized = 0.2 + (old_val - min_mood) / (max_mood - min_mood) * 0.6
            else:
                normalized = 0.5
            raw_moods[mood_key] = (round(normalized, 3), var)

    @staticmethod
    def _populate_ml_summary_scores(result: Dict[str, Any]) -> None:
        happy = result.get("moodHappy", 0.5)
        sad = result.get("moodSad", 0.5)
        party = result.get("moodParty", 0.5)
        result["valence"] = round(max(0.0, min(1.0, happy * 0.5 + party * 0.3 + (1 - sad) * 0.2)), 3)

        aggressive = result.get("moodAggressive", 0.5)
        relaxed = result.get("moodRelaxed", 0.5)
        acoustic = result.get("moodAcoustic", 0.5)
        electronic = result.get("moodElectronic", 0.5)
        result["arousal"] = round(
            max(0.0, min(1.0, aggressive * 0.35 + party * 0.25 + electronic * 0.2 + (1 - relaxed) * 0.1 + (1 - acoustic) * 0.1)),
            3,
        )

    def _populate_optional_ml_scores(self, result: Dict[str, Any], embeddings) -> None:
        voice_model = self.prediction_models.get("voice_instrumental")
        if voice_model is not None:
            voice_val, _ = self._safe_predict(voice_model, embeddings)
            result["instrumentalness"] = voice_val

        if "moodAcoustic" in result:
            result["acousticness"] = result["moodAcoustic"]

        dance_model = self.prediction_models.get("danceability")
        if dance_model is not None:
            dance_val, _ = self._safe_predict(dance_model, embeddings)
            result["danceabilityMl"] = dance_val

    def _extract_ml_features(self, audio_16k) -> Dict[str, Any]:
        if self.musicnn_model is None:
            raise RuntimeError("MusiCNN model not loaded")

        result: Dict[str, Any] = {}
        embeddings = self.musicnn_model(audio_16k)

        mood_map = {
            "mood_happy": "moodHappy",
            "mood_sad": "moodSad",
            "mood_relaxed": "moodRelaxed",
            "mood_aggressive": "moodAggressive",
            "mood_party": "moodParty",
            "mood_acoustic": "moodAcoustic",
            "mood_electronic": "moodElectronic",
        }

        raw_moods = self._collect_raw_moods(embeddings, mood_map)
        self._normalize_core_moods(raw_moods)

        for mood_key, (value, _) in raw_moods.items():
            result[mood_key] = value

        self._populate_ml_summary_scores(result)
        deam = self._predict_deam(audio_16k)
        if deam is not None:
            result["valence"] = deam["valence"]
            result["arousal"] = deam["arousal"]
        result["valenceSource"] = deam["valenceSource"] if deam is not None else "heuristic-fallback"
        result["arousalSource"] = deam["arousalSource"] if deam is not None else "heuristic-fallback"
        self._populate_optional_ml_scores(result, embeddings)

        return result

    @staticmethod
    def _bpm_valence_estimate(bpm: float) -> float:
        if not bpm:
            return 0.5
        if bpm >= 120:
            return min(0.8, 0.5 + (bpm - 120) / 200)
        if bpm <= 80:
            return max(0.2, 0.5 - (80 - bpm) / 100)
        return 0.5

    @staticmethod
    def _bpm_arousal_estimate(bpm: float) -> float:
        if not bpm:
            return 0.5
        return min(0.9, max(0.1, (bpm - 60) / 140))

    @staticmethod
    def _zcr_instrumental_estimate(zcr: float) -> float:
        if zcr < 0.05:
            return 0.7
        if zcr > 0.15:
            return 0.4
        return 0.5

    @staticmethod
    def _speechiness_estimate(zcr: float, spectral_centroid: float) -> float:
        if 0.08 < zcr < 0.2 and 0.1 < spectral_centroid < 0.4:
            return round(min(0.5, zcr * 3), 3)
        return 0.1

    def _apply_standard_estimates(self, result: Dict[str, Any], scale: str, bpm: float):
        result["analysisMode"] = "standard"

        energy = result.get("energy", 0.5) or 0.5
        dynamic_range = result.get("dynamicRange", 8) or 8
        danceability = result.get("danceability", 0.5) or 0.5
        spectral_centroid = result.get("_spectral_centroid", 0.5) or 0.5
        spectral_flatness = result.get("_spectral_flatness", -20) or -20
        zcr = result.get("_zcr", 0.1) or 0.1

        key_valence = 0.65 if scale == "major" else 0.35

        bpm_valence = self._bpm_valence_estimate(bpm)
        brightness_valence = min(1.0, spectral_centroid * 1.5)
        result["valence"] = round(
            key_valence * 0.4 + bpm_valence * 0.25 + brightness_valence * 0.2 + energy * 0.15,
            3,
        )

        bpm_arousal = self._bpm_arousal_estimate(bpm)
        energy_arousal = energy
        compression_arousal = max(0, min(1.0, 1 - (dynamic_range / 20)))
        brightness_arousal = min(1.0, spectral_centroid * 1.2)

        result["arousal"] = round(
            bpm_arousal * 0.35 + energy_arousal * 0.35 + brightness_arousal * 0.15 + compression_arousal * 0.15,
            3,
        )

        flatness_normalized = min(1.0, max(0, (spectral_flatness + 40) / 40))
        zcr_instrumental = self._zcr_instrumental_estimate(zcr)
        result["instrumentalness"] = round(flatness_normalized * 0.6 + zcr_instrumental * 0.4, 3)
        result["acousticness"] = round(min(1.0, dynamic_range / 12), 3)
        result["speechiness"] = self._speechiness_estimate(zcr, spectral_centroid)

        result["danceabilityMl"] = danceability

    @staticmethod
    def _add_model_mood_tags(
        tags: List[str],
        mood_happy: Optional[float],
        mood_sad: Optional[float],
        mood_relaxed: Optional[float],
        mood_aggressive: Optional[float],
    ) -> None:
        if mood_happy is not None and mood_happy >= 0.6:
            tags.extend(["happy", "uplifting"])
        if mood_sad is not None and mood_sad >= 0.6:
            tags.extend(["sad", "melancholic"])
        if mood_relaxed is not None and mood_relaxed >= 0.6:
            tags.extend(["relaxed", "chill"])
        if mood_aggressive is not None and mood_aggressive >= 0.6:
            tags.extend(["aggressive", "intense"])

    @staticmethod
    def _add_arousal_tags(tags: List[str], arousal: float) -> None:
        if arousal >= 0.7:
            tags.extend(["energetic", "upbeat"])
        elif arousal <= 0.3:
            tags.extend(["calm", "peaceful"])

    @staticmethod
    def _add_valence_tags(tags: List[str], valence: float) -> None:
        if "happy" in tags or "sad" in tags:
            return
        if valence >= 0.7:
            tags.extend(["happy", "uplifting"])
        elif valence <= 0.3:
            tags.extend(["sad", "melancholic"])

    @staticmethod
    def _add_tempo_tags(tags: List[str], bpm: float, danceability: float) -> None:
        if danceability >= 0.7:
            tags.extend(["dance", "groovy"])
        if bpm >= 140:
            tags.append("fast")
        elif bpm <= 80:
            tags.append("slow")

    @staticmethod
    def _add_contextual_tags(
        tags: List[str],
        *,
        key_scale: str,
        arousal: float,
        valence: float,
        bpm: float,
        mood_aggressive: Optional[float],
    ) -> None:
        if key_scale == "minor" and "happy" not in tags:
            tags.append("moody")
        if arousal >= 0.7 and bpm >= 120:
            tags.append("workout")
        if arousal <= 0.4 and valence <= 0.4:
            tags.append("atmospheric")
        if arousal <= 0.3 and bpm <= 90:
            tags.append("chill")
        if mood_aggressive is not None and mood_aggressive >= 0.5 and bpm >= 120:
            tags.append("intense")

    def _generate_mood_tags(self, features: Dict[str, Any]) -> List[str]:
        tags: List[str] = []

        bpm = features.get("bpm", 0) or 0
        valence = features.get("valence", 0.5) or 0.5
        arousal = features.get("arousal", 0.5) or 0.5
        danceability = features.get("danceability", 0.5) or 0.5
        key_scale = features.get("keyScale", "")

        mood_happy = features.get("moodHappy")
        mood_sad = features.get("moodSad")
        mood_relaxed = features.get("moodRelaxed")
        mood_aggressive = features.get("moodAggressive")

        self._add_model_mood_tags(tags, mood_happy, mood_sad, mood_relaxed, mood_aggressive)
        self._add_arousal_tags(tags, arousal)
        self._add_valence_tags(tags, valence)
        self._add_tempo_tags(tags, bpm, danceability)
        self._add_contextual_tags(
            tags,
            key_scale=key_scale,
            arousal=arousal,
            valence=valence,
            bpm=bpm,
            mood_aggressive=mood_aggressive,
        )

        return self._dedupe_in_order(tags)[:12]

    @staticmethod
    def _dedupe_in_order(values: List[str]) -> List[str]:
        deduped: List[str] = []
        seen = set()
        for value in values:
            if value in seen:
                continue
            seen.add(value)
            deduped.append(value)
        return deduped


def _encode_sonic_vector(sonic: Dict[str, Any]) -> Optional[str]:
    """Serialise the pooled vector as little-endian float32 base64.

    Only the pooled 1280-float vector crosses the process boundary, never the
    per-second frame matrix: 1280 float32 values are ~5 KB raw, whereas a
    600-frame unpooled matrix would be ~3 MB per track.
    """
    if np is None or not isinstance(sonic, dict):
        return None
    values = sonic.get("values")
    if not values:
        return None
    try:
        payload = np.asarray(values, dtype="<f4")
    except Exception:
        return None
    if not np.isfinite(payload).all():
        return None
    return base64.b64encode(payload.tobytes()).decode("ascii")


def build_payload(result: Dict[str, Any]) -> Dict[str, Any]:
    sonic = result.get("sonicEmbedding")
    payload = {
        "ok": True,
        "retryable": False,
        "AnalysisMode": result.get("analysisMode", "standard"),
        "AnalysisVersion": "musicnn-1" if result.get("analysisMode") == "enhanced" else "ffmpeg-basic-2",
        "Bpm": result.get("bpm"),
        "BeatsCount": result.get("beatsCount"),
        "Key": result.get("key"),
        "KeyScale": result.get("keyScale"),
        "KeyStrength": result.get("keyStrength"),
        "Danceability": result.get("danceability"),
        "Acousticness": result.get("acousticness"),
        "Instrumentalness": result.get("instrumentalness"),
        "Speechiness": result.get("speechiness"),
        "Genres": result.get("essentiaGenres", []),
        "EssentiaGenreEvidence": result.get("essentiaGenreEvidence", []),
        "GenreModel": result.get("genreModel"),
        "MoodTags": result.get("moodTags", []),
        "Happy": result.get("moodHappy"),
        "Sad": result.get("moodSad"),
        "Relaxed": result.get("moodRelaxed"),
        "Aggressive": result.get("moodAggressive"),
        "Party": result.get("moodParty"),
        "Acoustic": result.get("moodAcoustic"),
        "Electronic": result.get("moodElectronic"),
        "Approachability": None,
        "Engagement": None,
        "VoiceInstrumental": result.get("instrumentalness"),
        "TonalAtonal": None,
        "ValenceMl": result.get("valence"),
        "ArousalMl": result.get("arousal"),
        "ValenceSource": result.get("valenceSource"),
        "ArousalSource": result.get("arousalSource"),
        "DanceabilityMl": result.get("danceabilityMl"),
        "Loudness": result.get("loudness"),
        "DynamicComplexity": result.get("dynamicRange"),
        "AudioTruncated": bool(result.get("audioTruncated", False)),
    }
    if isinstance(sonic, dict):
        encoded = _encode_sonic_vector(sonic)
        if encoded is not None:
            payload["SonicEmbedding"] = {
                "ModelId": sonic.get("modelId"),
                "ModelVersion": sonic.get("modelVersion"),
                "EmbeddingVersion": sonic.get("embeddingVersion"),
                "Dimensions": int(sonic.get("dimensions") or 0),
                "Pooling": sonic.get("pooling"),
                "Normalization": sonic.get("normalization"),
                "DistanceMetric": sonic.get("distanceMetric"),
                "FrameCount": int(sonic.get("frameCount") or 0),
                "VectorBase64": encoded,
            }
    if result.get("sonicUnavailable"):
        payload["SonicUnavailable"] = True
    return payload


_process_analyzer: Optional[AudioAnalyzer] = None


def _init_worker_process(models_dir: str):
    global _process_analyzer
    _process_analyzer = AudioAnalyzer(models_dir)


def _analyze_track_in_process(entry: Dict[str, Any]) -> Dict[str, Any]:
    track_id_raw = entry.get("trackId")
    file_path_raw = entry.get("filePath")
    track_id = _normalize_track_id(track_id_raw)
    file_path = str(file_path_raw) if file_path_raw is not None else None

    if track_id is None:
        return {
            "trackId": None,
            "filePath": file_path,
            "ok": False,
            "errorCode": "VIBE_ANALYZER_INVALID_INPUT",
            "message": "Missing trackId for batch item.",
        }

    if not file_path:
        return {
            "trackId": track_id,
            "filePath": file_path,
            "ok": False,
            "errorCode": "VIBE_ANALYZER_INVALID_INPUT",
            "message": "Missing filePath for batch item.",
        }

    if not os.path.exists(file_path):
        return {
            "trackId": track_id,
            "filePath": file_path,
            "ok": False,
            "errorCode": "VIBE_ANALYZER_FILE_MISSING",
            "message": "Audio file not found.",
        }

    if _process_analyzer is None:
        return {
            "trackId": track_id,
            "filePath": file_path,
            "ok": False,
            "errorCode": "VIBE_ANALYZER_NOT_INITIALIZED",
            "message": "Analyzer worker failed to initialize.",
        }

    try:
        result = _process_analyzer.analyze(file_path)
        if "_error" in result:
            return {
                "trackId": track_id,
                "filePath": file_path,
                "ok": False,
                "errorCode": "VIBE_ANALYZER_FAILED",
                "message": str(result.get("_error")),
            }

        return {
            "trackId": track_id,
            "filePath": file_path,
            "ok": True,
            "payload": build_payload(result),
        }
    except Exception as exc:
        return {
            "trackId": track_id,
            "filePath": file_path,
            "ok": False,
            "errorCode": "VIBE_ANALYZER_FAILED",
            "message": str(exc),
        }


def _normalize_track_id(track_id_raw: Any) -> Optional[int]:
    if track_id_raw is None:
        return None
    try:
        return int(track_id_raw)
    except (TypeError, ValueError):
        return None


def _default_worker_count() -> int:
    cpu_count = os.cpu_count() or 4
    return max(2, min(8, cpu_count // 2))


def run_batch_analysis(
    batch_items: List[Dict[str, Any]],
    models_dir: str,
    workers: int,
    per_track_timeout_seconds: int,
    batch_timeout_seconds: int,
) -> Dict[str, Any]:
    normalized_workers = max(1, int(workers))
    normalized_track_timeout = max(1, int(per_track_timeout_seconds))
    normalized_batch_timeout = max(normalized_track_timeout, max(1, int(batch_timeout_seconds)))
    results: List[Dict[str, Any]] = []

    with ProcessPoolExecutor(
        max_workers=normalized_workers,
        initializer=_init_worker_process,
        initargs=(models_dir,),
    ) as executor:
        futures = {executor.submit(_analyze_track_in_process, item): item for item in batch_items}
        completed_futures = set()
        try:
            for future in as_completed(futures, timeout=normalized_batch_timeout):
                completed_futures.add(future)
                source = futures[future]
                track_id = source.get("trackId")
                file_path = source.get("filePath")
                try:
                    results.append(future.result(timeout=normalized_track_timeout))
                except Exception as exc:
                    results.append(
                        {
                            "trackId": track_id,
                            "filePath": file_path,
                            "ok": False,
                            "errorCode": "VIBE_ANALYZER_TIMEOUT",
                            "message": f"Timeout or error: {exc}",
                        }
                    )
        except TimeoutError:
            pass

        for future, source in futures.items():
            if future in completed_futures:
                continue

            track_id = source.get("trackId")
            file_path = source.get("filePath")
            future.cancel()
            results.append(
                {
                    "trackId": track_id,
                    "filePath": file_path,
                    "ok": False,
                    "errorCode": "VIBE_ANALYZER_TIMEOUT",
                    "message": f"Batch timeout after {normalized_batch_timeout} seconds.",
                }
            )

    return {
        "ok": True,
        "retryable": False,
        "results": results,
    }


def _probe_payload(models_dir: Optional[str] = None) -> Dict[str, Any]:
    missing_required, missing_optional = probe_capabilities()
    payload: Dict[str, Any] = {
        "ok": len(missing_required) == 0,
        "retryable": False,
        "errorCode": "ESSENTIA_MISSING_REQUIRED" if len(missing_required) > 0 else None,
        "message": None if len(missing_required) == 0 else "Missing required Essentia algorithms.",
        "missingRequired": missing_required,
        "missingOptional": missing_optional,
        "enhancedMode": False,
        "missingEnhancedModels": [],
        "loadedPredictionHeads": [],
        "genreModel": None,
        "genreModelLoaded": False,
        "missingGenreModelFiles": [],
    }

    if missing_required:
        return payload

    if not models_dir:
        return payload

    if not os.path.isdir(models_dir):
        payload["ok"] = False
        payload["errorCode"] = "VIBE_MODELS_MISSING"
        payload["message"] = f"Models directory not found: {models_dir}"
        return payload

    missing_enhanced = [
        file_name
        for file_name in REQUIRED_ENHANCED_MODELS
        if not os.path.exists(os.path.join(models_dir, file_name))
        or os.path.getsize(os.path.join(models_dir, file_name)) <= 0
    ]
    payload["missingEnhancedModels"] = missing_enhanced

    analyzer = AudioAnalyzer(models_dir)
    loaded_heads = sorted(analyzer.prediction_models.keys())
    payload["enhancedMode"] = analyzer.enhanced_mode
    payload["musicnnLoaded"] = analyzer.musicnn_model is not None
    payload["loadedPredictionHeads"] = loaded_heads

    # Report the acoustic genre branch that actually initialized so the host can
    # distinguish a full Discogs519 setup from a Discogs400 downgrade.
    payload["genreModel"] = analyzer.genre_model_name
    payload["genreModelLoaded"] = analyzer.genre_model_name is not None
    payload["missingGenreModelFiles"] = sorted(
        file_name
        for file_name in REQUIRED_GENRE519_MODELS
        if not os.path.exists(os.path.join(models_dir, file_name))
        or os.path.getsize(os.path.join(models_dir, file_name)) <= 0
    )

    if not analyzer.enhanced_mode:
        payload["ok"] = False
        payload["errorCode"] = "VIBE_ENHANCED_UNAVAILABLE"
        if missing_enhanced:
            payload["message"] = "Missing required enhanced analysis model files."
        else:
            payload["message"] = "Required enhanced analysis models exist but did not initialize."

    return payload


def _load_batch_items(batch_json_path: str) -> List[Dict[str, Any]]:
    if not os.path.exists(batch_json_path):
        raise FileNotFoundError(f"Batch request file not found: {batch_json_path}")

    with open(batch_json_path, "r", encoding="utf-8") as handle:
        raw_batch = json.load(handle)

    if not isinstance(raw_batch, list):
        raise ValueError("Batch request payload must be a JSON array.")

    return [item if isinstance(item, dict) else {} for item in raw_batch]


def _analyze_single_track(models_dir: str, file_path: str) -> Dict[str, Any]:
    analyzer = AudioAnalyzer(models_dir)
    result = analyzer.analyze(file_path)
    if "_error" in result:
        return {
            "ok": False,
            "retryable": False,
            "errorCode": "VIBE_ANALYZER_FAILED",
            "message": result.get("_error"),
        }
    return build_payload(result)


def _write_worker_response(payload: Dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def run_worker(models_dir: str) -> int:
    try:
        analyzer = AudioAnalyzer(models_dir)
        if not analyzer.enhanced_mode:
            raise RuntimeError("Required enhanced analysis models did not initialize.")
    except Exception as exc:
        sys.stderr.write(f"vibe analyzer worker initialization failed: {exc}\n")
        sys.stderr.flush()
        request_id = None
        request_line = sys.stdin.readline().strip()
        if request_line:
            try:
                request = json.loads(request_line)
                if isinstance(request, dict):
                    request_id = request.get("requestId")
            except (json.JSONDecodeError, TypeError, ValueError):
                pass
        _write_worker_response({
            "requestId": request_id,
            "ok": False,
            "retryable": True,
            "errorCode": "VIBE_ANALYZER_NOT_INITIALIZED",
            "message": str(exc),
        })
        return 1

    for request_line in sys.stdin:
        request_line = request_line.strip()
        if not request_line:
            continue

        request_id = None
        try:
            request = json.loads(request_line)
            if not isinstance(request, dict):
                raise ValueError("Worker request must be a JSON object.")

            request_id = request.get("requestId")
            file_path = request.get("filePath")
            if request_id is None:
                raise ValueError("requestId is required.")
            if not isinstance(file_path, str) or not file_path.strip():
                raise ValueError("filePath is required.")
            if not os.path.isfile(file_path):
                raise FileNotFoundError(f"Audio file not found: {file_path}")

            result = analyzer.analyze(file_path)
            if "_error" in result:
                _write_worker_response({
                    "requestId": request_id,
                    "ok": False,
                    "retryable": True,
                    "errorCode": "VIBE_ANALYZER_FAILED",
                    "message": str(result.get("_error")),
                })
                continue

            response = build_payload(result)
            response["requestId"] = request_id
            _write_worker_response(response)
        except (json.JSONDecodeError, TypeError, ValueError) as exc:
            _write_worker_response({
                "requestId": request_id,
                "ok": False,
                "retryable": False,
                "errorCode": "VIBE_ANALYZER_INVALID_INPUT",
                "message": str(exc),
            })
        except FileNotFoundError as exc:
            _write_worker_response({
                "requestId": request_id,
                "ok": False,
                "retryable": False,
                "errorCode": "VIBE_ANALYZER_FILE_MISSING",
                "message": str(exc),
            })
        except Exception as exc:
            sys.stderr.write(f"vibe analyzer worker request failed: {exc}\n")
            sys.stderr.flush()
            _write_worker_response({
                "requestId": request_id,
                "ok": False,
                "retryable": True,
                "errorCode": "VIBE_ANALYZER_FAILED",
                "message": str(exc),
            })

    return 0


def main():
    parser = argparse.ArgumentParser(description="Vibe Analyzer - lidify parity")
    parser.add_argument("--probe", action="store_true", help="Probe available Essentia capabilities")
    parser.add_argument("--file", help="Audio file path")
    parser.add_argument("--batch-json", help="Path to batch JSON file with trackId/filePath items")
    parser.add_argument("--models", help="Models directory path")
    parser.add_argument("--workers", type=int, default=_default_worker_count(), help="Batch worker count")
    parser.add_argument("--per-track-timeout-seconds", type=int, default=60, help="Per-track timeout in batch mode")
    parser.add_argument("--batch-timeout-seconds", type=int, default=300, help="Overall batch timeout")
    parser.add_argument("--worker", action="store_true", help="Run persistent JSON Lines worker mode")
    args = parser.parse_args()

    if args.probe:
        sys.stdout.write(json.dumps(_probe_payload(args.models)))
        return

    try:
        if not args.models:
            raise ValueError("--models is required unless --probe is set.")

        if not os.path.isdir(args.models):
            raise FileNotFoundError(f"Models directory not found: {args.models}")

        if args.worker:
            raise SystemExit(run_worker(args.models))

        if args.batch_json:
            batch_items = _load_batch_items(args.batch_json)
            batch_payload = run_batch_analysis(
                batch_items,
                args.models,
                args.workers,
                args.per_track_timeout_seconds,
                args.batch_timeout_seconds,
            )
            sys.stdout.write(json.dumps(batch_payload))
            return

        if not args.file:
            raise ValueError("--file is required for single-track analysis.")

        sys.stdout.write(json.dumps(_analyze_single_track(args.models, args.file)))
    except FileNotFoundError as exc:
        sys.stderr.write(f"vibe analyzer missing file: {exc}\n")
        sys.stdout.write(
            json.dumps(
                {
                    "ok": False,
                    "retryable": False,
                    "errorCode": "VIBE_MODELS_MISSING",
                    "message": str(exc),
                }
            )
        )
    except Exception as exc:
        sys.stderr.write(f"vibe analyzer failed: {exc}\n")
        sys.stdout.write(
            json.dumps(
                {
                    "ok": False,
                    "retryable": False,
                    "errorCode": "VIBE_ANALYZER_FAILED",
                    "message": str(exc),
                }
            )
        )


if __name__ == "__main__":
    main()
