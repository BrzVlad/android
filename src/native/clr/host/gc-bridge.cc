#include <cerrno>
#include <semaphore.h>

#include <jni.h>

#include <host/gc-bridge.hh>
#include <shared/helpers.hh>

using namespace xamarin::android;

void GCBridge::initialize_shared_args_semaphore () noexcept
{
	int ret = sem_init (&shared_args_semaphore, 0, 0);
	abort_unless (ret == 0, "Failed to initialize GC bridge semaphore");
}

void GCBridge::start_bridge_processing_thread () noexcept
{
	pthread_t thread {};
	int ret = pthread_create (&thread, nullptr, bridge_processing_thread_entry, nullptr);
	abort_unless (ret == 0, "Failed to create GC bridge processing thread");

	ret = pthread_detach (thread);
	abort_unless (ret == 0, "Failed to detach GC bridge processing thread");
}

void GCBridge::publish_shared_args (MarkCrossReferencesArgs *args) noexcept
{
#if !defined (XA_HOST_NATIVEAOT)
	MarkCrossReferencesArgs *no_pending_args = nullptr;
	bool pending_published = __atomic_compare_exchange_n (&pending_args, &no_pending_args, args, false, __ATOMIC_RELEASE, __ATOMIC_RELAXED);
	abort_unless (pending_published, "A GC bridge argument block is already pending completion");
#endif

	MarkCrossReferencesArgs *expected = nullptr;
	bool published = __atomic_compare_exchange_n (&shared_args, &expected, args, false, __ATOMIC_RELEASE, __ATOMIC_RELAXED);
	abort_unless (published, "A GC bridge argument block is already pending");

	int ret = sem_post (&shared_args_semaphore);
	abort_unless (ret == 0, "Failed to release GC bridge semaphore");
}

#if !defined (XA_HOST_NATIVEAOT)
auto GCBridge::get_pending_args () noexcept -> MarkCrossReferencesArgs*
{
	return __atomic_load_n (&pending_args, __ATOMIC_ACQUIRE);
}

void GCBridge::clear_pending_args (MarkCrossReferencesArgs *args) noexcept
{
	bool cleared = __atomic_compare_exchange_n (&pending_args, &args, nullptr, false, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED);
	abort_unless (cleared, "The completed GC bridge argument block is not pending");
}

extern "C" [[gnu::visibility("default")]]
auto android_get_pending_gc_bridge_args () noexcept -> MarkCrossReferencesArgs*
{
	return GCBridge::get_pending_args ();
}

extern "C" [[gnu::visibility("default")]]
void android_clear_pending_gc_bridge_args (MarkCrossReferencesArgs *args) noexcept
{
	GCBridge::clear_pending_args (args);
}

extern "C" [[gnu::visibility("default")]]
int android_get_gc_bridge_reference_type (JNIEnv *env, jobject reference) noexcept
{
	return static_cast<int> (env->GetObjectRefType (reference));
}
#endif

auto GCBridge::wait_for_shared_args () noexcept -> MarkCrossReferencesArgs*
{
	int ret;
	do {
		ret = sem_wait (&shared_args_semaphore);
	} while (ret == -1 && errno == EINTR);
	abort_unless (ret == 0, "Failed to acquire GC bridge semaphore");

	MarkCrossReferencesArgs *args = __atomic_exchange_n (&shared_args, nullptr, __ATOMIC_ACQUIRE);
	abort_unless (args != nullptr, "GC bridge semaphore was released without an argument block");

	return args;
}

void GCBridge::mark_cross_references (MarkCrossReferencesArgs *args) noexcept
{
	abort_if_invalid_pointer_argument (args, "args");
	abort_unless (args->Components != nullptr || args->ComponentCount == 0, "Components must not be null if ComponentCount is greater than 0");
	abort_unless (args->CrossReferences != nullptr || args->CrossReferenceCount == 0, "CrossReferences must not be null if CrossReferenceCount is greater than 0");

	publish_shared_args (args);
}

void GCBridge::bridge_processing () noexcept
{
	abort_unless (bridge_processing_callback != nullptr, "GC bridge processing callback is not set");

	while (true) {
		MarkCrossReferencesArgs *args = wait_for_shared_args ();
		bridge_processing_callback (args);
	}
}

auto GCBridge::bridge_processing_thread_entry ([[maybe_unused]] void *arg) noexcept -> void*
{
	bridge_processing ();
	return nullptr;
}
