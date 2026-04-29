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
import json
import os
import sys
import multiprocessing
from concurrent.futures import ProcessPoolExecutor, TimeoutError, as_completed
from typing import Any, Dict, List, Optional, Tuple

try:
    multiprocessing.set_start_method("spawn", force=True)
except RuntimeError:
    pass


# Essentia imports (graceful fallback)
ESSENTIA_AVAILABLE = False
es = None
try:
    import essentia  # type: ignore
    essentia.log.warningActive = False
    essentia.log.infoActive = False
    import essentia.standard as es  # type: ignore
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


REQUIRED_ENHANCED_MODELS = [
    "msd-musicnn-1.pb",
    "mood_happy-msd-musicnn-1.pb",
    "mood_sad-msd-musicnn-1.pb",
    "mood_relaxed-msd-musicnn-1.pb",
    "mood_aggressive-msd-musicnn-1.pb",
]


class AudioAnalyzer:
    """Lidify audio analysis core (ported)."""

    def __init__(self, models_dir: str):
        self.models_dir = models_dir
        self.enhanced_mode = False
        self.musicnn_model = None
        self.prediction_models: Dict[str, Any] = {}
        self.effnet_extractor = None
        self.genre_predictor = None
        self.genre_labels: List[str] = []

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

    def _extract_essentia_genres(self, audio_16k) -> List[str]:
        if self.effnet_extractor is None or self.genre_predictor is None or np is None:
            return []

        try:
            effnet_embeddings = self.effnet_extractor(audio_16k)
            scores = np.array(self.genre_predictor(effnet_embeddings))
            average_scores = scores.mean(axis=0) if scores.ndim == 2 else scores.reshape(-1)
            if average_scores.size == 0:
                return []

            top_indices = np.argsort(average_scores)[::-1][:8]
            genres: List[str] = []
            for index in top_indices:
                if float(average_scores[index]) < 0.15:
                    continue
                if self.genre_labels and index < len(self.genre_labels):
                    genres.append(self.genre_labels[index])
                else:
                    genres.append(f"genre_{index}")

            return genres
        except Exception:
            return []

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

    def _load_ml_models(self):
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

        audio_44k = self.load_audio(file_path, 44100)
        audio_16k = self.load_audio(file_path, 16000)
        if audio_44k is None or audio_16k is None:
            result["_error"] = "Unable to decode audio"
            return result

        try:
            result.update(self._extract_core_audio_metrics(audio_44k))
            bpm = result.get("bpm")
            scale = result.get("keyScale")

            if self.enhanced_mode:
                try:
                    ml_features = self._extract_ml_features(audio_16k)
                    result.update(ml_features)
                    result["analysisMode"] = "enhanced"
                except Exception:
                    self._apply_standard_estimates(result, scale, bpm)
            else:
                self._apply_standard_estimates(result, scale, bpm)

            result["essentiaGenres"] = self._extract_essentia_genres(audio_16k)
            result["moodTags"] = self._generate_mood_tags(result)
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


def build_payload(result: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "ok": True,
        "retryable": False,
        "AnalysisMode": result.get("analysisMode", "standard"),
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
        "DanceabilityMl": result.get("danceabilityMl"),
        "Loudness": result.get("loudness"),
        "DynamicComplexity": result.get("dynamicRange"),
    }


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


def main():
    parser = argparse.ArgumentParser(description="Vibe Analyzer - lidify parity")
    parser.add_argument("--probe", action="store_true", help="Probe available Essentia capabilities")
    parser.add_argument("--file", help="Audio file path")
    parser.add_argument("--batch-json", help="Path to batch JSON file with trackId/filePath items")
    parser.add_argument("--models", help="Models directory path")
    parser.add_argument("--workers", type=int, default=_default_worker_count(), help="Batch worker count")
    parser.add_argument("--per-track-timeout-seconds", type=int, default=60, help="Per-track timeout in batch mode")
    parser.add_argument("--batch-timeout-seconds", type=int, default=300, help="Overall batch timeout")
    args = parser.parse_args()

    if args.probe:
        sys.stdout.write(json.dumps(_probe_payload(args.models)))
        return

    try:
        if not args.models:
            raise ValueError("--models is required unless --probe is set.")

        if not os.path.isdir(args.models):
            raise FileNotFoundError(f"Models directory not found: {args.models}")

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
