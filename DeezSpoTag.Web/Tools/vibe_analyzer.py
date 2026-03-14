#!/usr/bin/env python3
"""
Vibe Analyzer - Essentia-based audio analysis for mood, genre, and audio features.

Supports models from essentia.upf.edu/models.html:
- MSD-MusiCNN for embeddings + mood/vibe classification heads
- Discogs-EffNet for embeddings + approachability/engagement/genre heads
- DEAM arousal/valence (MusiCNN-based)
"""
import argparse
import json
import os
import sys

try:
    # Keep TensorFlow/Essentia noise down in CPU-only Docker environments.
    # (Not fatal if a GPU isn't present; we just want less log spam.)
    os.environ.setdefault("CUDA_VISIBLE_DEVICES", "")
    os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

    import essentia.standard as es
except Exception as exc:
    sys.stderr.write(f"essentia import failed: {exc}\n")
    sys.stdout.write(
        json.dumps(
            {
                "ok": False,
                "retryable": False,
                "errorCode": "ESSENTIA_IMPORT_FAILED",
                "message": str(exc),
            }
        )
    )
    sys.exit(0)


def _required(name: str):
    value = getattr(es, name, None)
    if value is None:
        raise RuntimeError(f"essentia missing required algorithm: {name}")
    return value


def _optional(name: str):
    return getattr(es, name, None)


def probe_capabilities():
    required = [
        "MonoLoader",
        "TensorflowPredictMusiCNN",
        "TensorflowPredict2D",
    ]
    optional = [
        "TensorflowPredictEffnetDiscogs",
        "TensorflowPredictVGGish",
        "RhythmExtractor2013",
        "KeyExtractor",
        "Danceability",
        "Loudness",
        "DynamicComplexity",
    ]

    missing_required = [name for name in required if getattr(es, name, None) is None]
    missing_optional = [name for name in optional if getattr(es, name, None) is None]
    return missing_required, missing_optional


MonoLoader = _required("MonoLoader")
TensorflowPredictMusiCNN = _required("TensorflowPredictMusiCNN")
TensorflowPredict2D = _required("TensorflowPredict2D")

TensorflowPredictEffnetDiscogs = _optional("TensorflowPredictEffnetDiscogs")
TensorflowPredictVGGish = _optional("TensorflowPredictVGGish")
RhythmExtractor2013 = _optional("RhythmExtractor2013")
KeyExtractor = _optional("KeyExtractor")
EssentiaDanceability = _optional("Danceability")
Loudness = _optional("Loudness")
DynamicComplexity = _optional("DynamicComplexity")


def load_audio(path):
    loader = MonoLoader(filename=path, sampleRate=16000)
    return loader()


def load_genre_labels(models_dir):
    """Load Discogs genre labels from metadata JSON if available."""
    import json as _json
    meta_path = os.path.join(models_dir, "genre_discogs400-discogs-effnet-1.json")
    if os.path.exists(meta_path):
        try:
            with open(meta_path) as f:
                meta = _json.load(f)
            return meta.get("classes") or []
        except Exception:
            pass
    return []


def load_models(models_dir):
    """Load all available Essentia models from the models directory."""
    def resolve_model_path(*candidates):
        for candidate in candidates:
            candidate_path = os.path.join(models_dir, candidate)
            if os.path.exists(candidate_path):
                return candidate_path
        return None

    # Base MusiCNN embedding model (optional for standard fallback mode)
    base_model = resolve_model_path("msd-musicnn-1.pb")
    musicnn = None
    if base_model is not None:
        musicnn = TensorflowPredictMusiCNN(graphFilename=base_model, output="model/dense/BiasAdd")
    else:
        sys.stderr.write("Warning: Missing base MusiCNN model; standard mode will be used.\n")

    # Discogs-EffNet embedding extractor (optional, needed for approachability/engagement/genre)
    effnet_model = os.path.join(models_dir, "discogs-effnet-bs64-1.pb")
    effnet_extractor = None
    if TensorflowPredictEffnetDiscogs is not None and os.path.exists(effnet_model):
        effnet_extractor = TensorflowPredictEffnetDiscogs(
            graphFilename=effnet_model, output="PartitionedCall:1"
        )

    # Genre model (optional, uses EffNet embeddings)
    genre_model = os.path.join(models_dir, "genre_discogs400-discogs-effnet-1.pb")
    genre_predictor = None
    if effnet_extractor is not None and os.path.exists(genre_model):
        genre_predictor = TensorflowPredict2D(
            graphFilename=genre_model,
            input="serving_default_model_Placeholder",
            output="PartitionedCall"
        )

    # Mood classification heads (MusiCNN-based)
    mood_heads = {
        "happy": "mood_happy-msd-musicnn-1.pb",
        "sad": "mood_sad-msd-musicnn-1.pb",
        "relaxed": "mood_relaxed-msd-musicnn-1.pb",
        "aggressive": "mood_aggressive-msd-musicnn-1.pb",
        "party": "mood_party-msd-musicnn-1.pb",
        "acoustic": "mood_acoustic-msd-musicnn-1.pb",
        "electronic": "mood_electronic-msd-musicnn-1.pb",
    }

    # Vibe analysis heads (MusiCNN-based)
    vibe_heads = {
        "voice_instrumental": "voice_instrumental-msd-musicnn-1.pb",
        "danceability": "danceability-msd-musicnn-1.pb",
        "tonal_atonal": "tonal_atonal-msd-musicnn-1.pb",
    }

    # DEAM arousal/valence model (combined output, MusiCNN-based)
    deam_model_path = resolve_model_path("deam-msd-musicnn-2.pb", "deam-msd-musicnn-1.pb")
    deam_predictor = None
    if deam_model_path is not None:
        deam_predictor = TensorflowPredict2D(graphFilename=deam_model_path, output="model/Identity")

    # Approachability/Engagement (EffNet-based, optional)
    effnet_vibe_heads = {}
    if effnet_extractor is not None:
        for key, filename in {
            "approachability": "approachability_regression-discogs-effnet-1.pb",
            "engagement": "engagement_regression-discogs-effnet-1.pb",
        }.items():
            model_path = os.path.join(models_dir, filename)
            if os.path.exists(model_path):
                effnet_vibe_heads[key] = TensorflowPredict2D(
                    graphFilename=model_path, output="model/Identity"
                )

    # Load mood predictors
    mood_predictors = {}
    for key, filename in mood_heads.items():
        model_path = resolve_model_path(
            filename,
            filename.replace("-msd-musicnn-1.pb", "-discogs-effnet-1.pb"),
        )
        if musicnn is not None and model_path is not None:
            try:
                mood_predictors[key] = TensorflowPredict2D(graphFilename=model_path, output="model/Softmax")
            except Exception as e:
                sys.stderr.write(f"Warning: Failed to load mood model for {key}: {e}\n")
        else:
            sys.stderr.write(f"Warning: Missing mood model {filename}\n")

    # Load vibe predictors (optional, MusiCNN-based)
    vibe_predictors = {}
    for key, filename in vibe_heads.items():
        model_path = resolve_model_path(
            filename,
            filename.replace("-msd-musicnn-1.pb", "-discogs-effnet-1.pb"),
        )
        if musicnn is not None and model_path is not None:
            try:
                vibe_predictors[key] = TensorflowPredict2D(graphFilename=model_path, output="model/Softmax")
            except Exception as e:
                sys.stderr.write(f"Warning: Failed to load vibe model for {key}: {e}\n")

    core_moods = {"happy", "sad", "relaxed", "aggressive"}
    enhanced_ready = core_moods.issubset(set(mood_predictors.keys()))
    genre_labels = load_genre_labels(models_dir)
    return musicnn, mood_predictors, vibe_predictors, genre_predictor, effnet_extractor, effnet_vibe_heads, deam_predictor, genre_labels, enhanced_ready


def _avg_frames(arr):
    """Average a (frames, classes) or (classes,) array to a 1-D numpy array."""
    import numpy as np
    arr = np.array(arr)
    if arr.ndim == 2:
        return arr.mean(axis=0)
    return arr


def predict_scores(audio, musicnn, mood_predictors, vibe_predictors, genre_predictor,
                   effnet_extractor, effnet_vibe_heads, deam_predictor, genre_labels=None):
    """Run predictions using all loaded models."""
    import numpy as np

    embeddings = None
    if musicnn is not None and (len(mood_predictors) > 0 or len(vibe_predictors) > 0 or deam_predictor is not None):
        try:
            embeddings = musicnn(audio)
        except Exception as e:
            sys.stderr.write(f"Warning: Failed to compute MusiCNN embeddings: {e}\n")

    # Mood scores (MusiCNN-based binary classifiers: index 1 = positive class)
    mood_scores = {}
    if embeddings is not None:
        for key, predictor in mood_predictors.items():
            try:
                avg = _avg_frames(predictor(embeddings))
                score = float(avg[1]) if len(avg) > 1 else float(avg[0])
                mood_scores[key] = max(0.0, min(1.0, score))
            except Exception as e:
                sys.stderr.write(f"Warning: Failed to predict {key}: {e}\n")
                mood_scores[key] = None

    # Vibe scores (MusiCNN-based)
    vibe_scores = {}
    if embeddings is not None:
        for key, predictor in vibe_predictors.items():
            try:
                avg = _avg_frames(predictor(embeddings))
                # Binary classifiers (voice_instrumental, tonal_atonal, danceability): index 1 = positive
                score = float(avg[1]) if len(avg) > 1 else float(avg[0])
                vibe_scores[key] = max(0.0, min(1.0, score))
            except Exception as e:
                sys.stderr.write(f"Warning: Failed to predict {key}: {e}\n")
                vibe_scores[key] = None

    # DEAM arousal/valence (combined 2-value output per frame)
    if deam_predictor is not None and embeddings is not None:
        try:
            avg = _avg_frames(deam_predictor(embeddings))
            if len(avg) >= 2:
                vibe_scores["valence"] = max(0.0, min(1.0, float(avg[0])))
                vibe_scores["arousal"] = max(0.0, min(1.0, float(avg[1])))
            elif len(avg) == 1:
                vibe_scores["valence"] = max(0.0, min(1.0, float(avg[0])))
        except Exception as e:
            sys.stderr.write(f"Warning: Failed to predict DEAM arousal/valence: {e}\n")

    # EffNet-based predictions (approachability, engagement) — regression, single value
    if effnet_extractor is not None and len(effnet_vibe_heads) > 0:
        try:
            effnet_embeddings = effnet_extractor(audio)
            for key, predictor in effnet_vibe_heads.items():
                try:
                    avg = _avg_frames(predictor(effnet_embeddings))
                    score = float(avg[0])
                    vibe_scores[key] = max(0.0, min(1.0, score))
                except Exception as e:
                    sys.stderr.write(f"Warning: Failed to predict {key}: {e}\n")
        except Exception as e:
            sys.stderr.write(f"Warning: Failed to compute EffNet embeddings: {e}\n")

    # Genre classification (EffNet-based)
    genres = []
    if genre_predictor is not None and effnet_extractor is not None:
        try:
            effnet_embeddings = effnet_extractor(audio)
            avg_scores = _avg_frames(genre_predictor(effnet_embeddings))
            top_indices = np.argsort(avg_scores)[::-1][:8]
            for i in top_indices:
                if avg_scores[i] >= 0.15:
                    if genre_labels and i < len(genre_labels):
                        genres.append(genre_labels[i])
                    else:
                        genres.append(f"genre_{i}")
        except Exception as e:
            sys.stderr.write(f"Warning: Failed to predict genres: {e}\n")

    return mood_scores, vibe_scores, genres


def extract_features(audio, sample_rate):
    """Extract audio features using Essentia algorithms."""

    features = {
        "bpm": None,
        "beatsCount": None,
        "key": None,
        "keyScale": None,
        "keyStrength": None,
        "danceabilityEssentia": None,
        "loudness": None,
        "dynamicComplexity": None,
    }

    # BPM and beats
    if RhythmExtractor2013 is not None:
        try:
            rhythm = RhythmExtractor2013(method="multifeature")
            bpm, beats, _, _, _ = rhythm(audio)
            features["bpm"] = float(bpm) if bpm > 0 else None
            features["beatsCount"] = int(len(beats)) if beats is not None else None
        except Exception as e:
            sys.stderr.write(f"Warning: Rhythm extraction failed: {e}\n")

    # Key detection
    if KeyExtractor is not None:
        try:
            key_extractor = KeyExtractor()
            key, key_scale, key_strength = key_extractor(audio)
            features["key"] = key if key else None
            features["keyScale"] = key_scale if key_scale else None
            features["keyStrength"] = float(key_strength) if key_strength else None
        except Exception as e:
            sys.stderr.write(f"Warning: Key extraction failed: {e}\n")

    # Danceability (Essentia algorithm, not ML model)
    if EssentiaDanceability is not None:
        try:
            danceability_algo = EssentiaDanceability()
            danceability, _ = danceability_algo(audio)
            features["danceabilityEssentia"] = float(danceability)
        except Exception as e:
            sys.stderr.write(f"Warning: Danceability extraction failed: {e}\n")

    # Loudness
    if Loudness is not None:
        try:
            loudness_algo = Loudness()
            loudness = loudness_algo(audio)
            features["loudness"] = float(loudness)
        except Exception as e:
            sys.stderr.write(f"Warning: Loudness extraction failed: {e}\n")

    # Dynamic complexity
    if DynamicComplexity is not None:
        try:
            dc_algo = DynamicComplexity()
            dc, _ = dc_algo(audio)
            features["dynamicComplexity"] = float(dc)
        except Exception as e:
            sys.stderr.write(f"Warning: Dynamic complexity extraction failed: {e}\n")

    return features


def main():
    parser = argparse.ArgumentParser(description="Vibe Analyzer - Essentia-based audio analysis")
    parser.add_argument("--probe", action="store_true", help="Probe for available capabilities")
    parser.add_argument("--file", help="Audio file to analyze")
    parser.add_argument("--models", help="Path to models directory")
    args = parser.parse_args()

    try:
        if args.probe:
            missing_required, missing_optional = probe_capabilities()
            sys.stdout.write(
                json.dumps(
                    {
                        "ok": len(missing_required) == 0,
                        "retryable": False,
                        "errorCode": "ESSENTIA_MISSING_REQUIRED" if len(missing_required) > 0 else None,
                        "message": None if len(missing_required) == 0 else "Missing required Essentia algorithms.",
                        "missingRequired": missing_required,
                        "missingOptional": missing_optional,
                    }
                )
            )
            return

        if not args.file or not args.models:
            raise ValueError("--file and --models are required unless --probe is set.")

        # Load audio
        audio = load_audio(args.file)

        # Load models
        musicnn, mood_predictors, vibe_predictors, genre_predictor, \
            effnet_extractor, effnet_vibe_heads, deam_predictor, genre_labels, enhanced_ready = load_models(args.models)

        # Run predictions
        mood_scores, vibe_scores, genres = predict_scores(
            audio, musicnn, mood_predictors, vibe_predictors, genre_predictor,
            effnet_extractor, effnet_vibe_heads, deam_predictor, genre_labels
        )

        core_mood_keys = ("happy", "sad", "relaxed", "aggressive")
        core_moods_available = all(mood_scores.get(key) is not None for key in core_mood_keys)
        analysis_mode = "enhanced" if enhanced_ready and core_moods_available else "standard"

        # Extract audio features
        features = extract_features(audio, 16000)

        # Build output payload
        payload = {
            "ok": True,
            "retryable": False,

            # Mood scores
            "Happy": mood_scores.get("happy"),
            "Sad": mood_scores.get("sad"),
            "Relaxed": mood_scores.get("relaxed"),
            "Aggressive": mood_scores.get("aggressive"),
            "Party": mood_scores.get("party"),
            "Acoustic": mood_scores.get("acoustic"),
            "Electronic": mood_scores.get("electronic"),

            # Vibe scores
            "Approachability": vibe_scores.get("approachability"),
            "Engagement": vibe_scores.get("engagement"),
            "VoiceInstrumental": vibe_scores.get("voice_instrumental"),  # 0=instrumental, 1=voice
            "DanceabilityMl": vibe_scores.get("danceability"),
            "ArousalMl": vibe_scores.get("arousal"),
            "ValenceMl": vibe_scores.get("valence"),
            "TonalAtonal": vibe_scores.get("tonal_atonal"),  # 0=tonal, 1=atonal

            # Audio features
            "Bpm": features.get("bpm"),
            "BeatsCount": features.get("beatsCount"),
            "Key": features.get("key"),
            "KeyScale": features.get("keyScale"),
            "KeyStrength": features.get("keyStrength"),
            "Danceability": features.get("danceabilityEssentia"),
            "Loudness": features.get("loudness"),
            "DynamicComplexity": features.get("dynamicComplexity"),

            # Genres
            "Genres": genres,

            # Analysis metadata
            "AnalysisMode": analysis_mode,
            "ModelsLoaded": {
                "moods": list(mood_predictors.keys()),
                "vibes": list(vibe_predictors.keys()),
                "effnet_vibes": list(effnet_vibe_heads.keys()) if effnet_vibe_heads else [],
                "deam": deam_predictor is not None,
                "genre": genre_predictor is not None,
                "effnet": effnet_extractor is not None,
            },
        }

        sys.stdout.write(json.dumps(payload))

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
        sys.exit(0)
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
        sys.exit(0)


if __name__ == "__main__":
    main()
