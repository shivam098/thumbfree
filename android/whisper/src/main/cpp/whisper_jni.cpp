// JNI bridge to whisper.cpp for app.thumbfree.whisper.WhisperNative. Kept small: load, transcribe, abort, free.
#include <jni.h>

#include <atomic>
#include <string>

#include <android/log.h>

#include "whisper.h"

namespace {

constexpr const char *kTag = "ThumbFreeWhisper";

struct Model {
    whisper_context *ctx = nullptr;
    std::atomic<bool> abort{false};
};

bool ShouldAbort(void *user_data) {
    return static_cast<Model *>(user_data)->abort.load(std::memory_order_relaxed);
}

// Only warnings and errors reach logcat; whisper.cpp is chatty at info level. Never logs transcribed text.
void Log(enum ggml_log_level level, const char *text, void *) {
    if (level == GGML_LOG_LEVEL_WARN) {
        __android_log_write(ANDROID_LOG_WARN, kTag, text);
    } else if (level == GGML_LOG_LEVEL_ERROR) {
        __android_log_write(ANDROID_LOG_ERROR, kTag, text);
    }
}

Model *FromHandle(jlong handle) { return reinterpret_cast<Model *>(handle); }

}  // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_app_thumbfree_whisper_WhisperNative_init(JNIEnv *env, jclass, jstring path) {
    whisper_log_set(Log, nullptr);
    const char *model_path = env->GetStringUTFChars(path, nullptr);
    whisper_context_params params = whisper_context_default_params();
    params.use_gpu = false;
    whisper_context *ctx = whisper_init_from_file_with_params(model_path, params);
    env->ReleaseStringUTFChars(path, model_path);
    if (ctx == nullptr) {
        return 0;
    }
    auto *model = new Model();
    model->ctx = ctx;
    return reinterpret_cast<jlong>(model);
}

extern "C" JNIEXPORT void JNICALL
Java_app_thumbfree_whisper_WhisperNative_free(JNIEnv *, jclass, jlong handle) {
    Model *model = FromHandle(handle);
    if (model != nullptr) {
        whisper_free(model->ctx);
        delete model;
    }
}

extern "C" JNIEXPORT void JNICALL
Java_app_thumbfree_whisper_WhisperNative_setAbort(JNIEnv *, jclass, jlong handle, jboolean abort) {
    Model *model = FromHandle(handle);
    if (model != nullptr) {
        model->abort.store(abort == JNI_TRUE, std::memory_order_relaxed);
    }
}

// Returns the UTF-8 text (as bytes: JNI's NewStringUTF can't take every UTF-8 sequence), or null if the call was
// aborted or failed.
extern "C" JNIEXPORT jbyteArray JNICALL
Java_app_thumbfree_whisper_WhisperNative_transcribe(JNIEnv *env, jclass, jlong handle, jfloatArray samples,
                                                     jint threads, jint audio_ctx, jint max_tokens,
                                                     jboolean temperature_fallback, jstring language,
                                                     jstring prompt) {
    Model *model = FromHandle(handle);
    if (model == nullptr || model->ctx == nullptr) {
        return nullptr;
    }

    whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = threads;
    params.translate = false;
    params.no_context = true;
    params.no_timestamps = true;
    params.single_segment = false;
    params.print_special = false;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.suppress_blank = true;
    params.detect_language = false;
    params.audio_ctx = audio_ctx;
    params.max_tokens = max_tokens;
    if (temperature_fallback != JNI_TRUE) {
        params.temperature_inc = 0.0f;
    }
    params.abort_callback = ShouldAbort;
    params.abort_callback_user_data = model;

    const char *lang = language != nullptr ? env->GetStringUTFChars(language, nullptr) : nullptr;
    const char *initial_prompt = prompt != nullptr ? env->GetStringUTFChars(prompt, nullptr) : nullptr;
    params.language = lang != nullptr ? lang : "en";
    params.initial_prompt = initial_prompt;

    const jsize count = env->GetArrayLength(samples);
    jfloat *data = env->GetFloatArrayElements(samples, nullptr);
    const int rc = whisper_full(model->ctx, params, data, count);
    env->ReleaseFloatArrayElements(samples, data, JNI_ABORT);
    if (lang != nullptr) env->ReleaseStringUTFChars(language, lang);
    if (initial_prompt != nullptr) env->ReleaseStringUTFChars(prompt, initial_prompt);

    if (rc != 0 || model->abort.load(std::memory_order_relaxed)) {
        return nullptr;
    }

    std::string text;
    const int segments = whisper_full_n_segments(model->ctx);
    for (int i = 0; i < segments; ++i) {
        text += whisper_full_get_segment_text(model->ctx, i);
    }

    jbyteArray out = env->NewByteArray(static_cast<jsize>(text.size()));
    env->SetByteArrayRegion(out, 0, static_cast<jsize>(text.size()), reinterpret_cast<const jbyte *>(text.data()));
    return out;
}

extern "C" JNIEXPORT jstring JNICALL
Java_app_thumbfree_whisper_WhisperNative_systemInfo(JNIEnv *env, jclass) {
    return env->NewStringUTF(whisper_print_system_info());
}
