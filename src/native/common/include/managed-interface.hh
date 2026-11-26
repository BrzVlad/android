#pragma once

#include <cstddef>
#include <cstdint>

#include <jni.h>
#include <runtime-base/binary-blob-loader.hh>

struct MarkCrossReferencesArgs;

namespace xamarin::android {
	using jnienv_propagate_uncaught_exception_fn = void (*)(JNIEnv *env, jobject javaThread, jthrowable javaException);
	using jnienv_gc_bridge_fn = void (*)(MarkCrossReferencesArgs*);
	using jnienv_initialize_gc_bridge_fn = jnienv_gc_bridge_fn (*)(jnienv_gc_bridge_fn);

	// NOTE: Keep this in sync with managed side in src/Mono.Android/Android.Runtime/JNIEnvInit.cs
	struct JnienvInitializeArgs {
		JavaVM         *javaVm;
		JNIEnv         *env;
		jobject         grefLoader;
		unsigned int    logCategories;
		int             grefGcThreshold;
		jobject         grefIGCUserPeer;
		uint8_t         brokenExceptionTransitions;
		int             packageNamingPolicy;
		uint8_t         boundExceptionType;
		const BinaryBlobPayload *jniRemappingData;
		jnienv_propagate_uncaught_exception_fn propagateUncaughtExceptionFn;
		int              maxGrefCount;
		jnienv_initialize_gc_bridge_fn initializeGCBridge;
		// Nonzero from the GC bridge callback until managed bridge processing has finished the round.
		uint32_t       *gcBridgeProcessingActive;
	};

	static_assert (offsetof (JnienvInitializeArgs, initializeGCBridge) == (sizeof (void*) == 8 ? 80 : 48));
	static_assert (offsetof (JnienvInitializeArgs, gcBridgeProcessingActive) == (sizeof (void*) == 8 ? 88 : 52));
	static_assert (sizeof (JnienvInitializeArgs) == (sizeof (void*) == 8 ? 96 : 56));

	// Keep the enum values in sync with those in src/Mono.Android/AndroidRuntime/BoundExceptionType.cs
	enum class BoundExceptionType : uint8_t
	{
		System = 0x00,
		Java   = 0x01,
	};

	using jnienv_initialize_fn = void (*) (JnienvInitializeArgs*);
}
