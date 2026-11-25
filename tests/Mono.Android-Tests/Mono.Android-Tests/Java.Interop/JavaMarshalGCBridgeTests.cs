using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Java;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Java.Interop;
using Java.InteropTests;
using Microsoft.Android.Runtime;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Xamarin.Android.RuntimeTests;

[TestClass]
[TestCategory ("RuntimeInitialization")]
public class JavaMarshalGCBridgeTests
{
	readonly TestContext testContext;

	public JavaMarshalGCBridgeTests (TestContext testContext)
	{
		this.testContext = testContext;
	}

	// A static class cannot be used as the first parameter's type.
	[UnsafeAccessor (UnsafeAccessorKind.StaticMethod, Name = nameof (JavaMarshalGCBridge.RequiresTemporaryPeer))]
	static extern bool RequiresTemporaryPeer (
		[UnsafeAccessorType ("Microsoft.Android.Runtime.JavaMarshalGCBridge, Mono.Android")] object target,
		nuint componentCount);

	[UnsafeAccessor (UnsafeAccessorKind.StaticMethod, Name = "PrepareComponentsAndCrossReferences")]
	static extern unsafe void PrepareComponentsAndCrossReferences (
		[UnsafeAccessorType ("Microsoft.Android.Runtime.JavaMarshalGCBridge, Mono.Android")] object target,
		MarkCrossReferencesArgs* args);

	[UnsafeAccessor (UnsafeAccessorKind.StaticMethod, Name = "get_BridgeJni")]
	static extern JavaMarshalGCBridgeJni GetBridgeJni (
		[UnsafeAccessorType ("Microsoft.Android.Runtime.JavaMarshalGCBridge, Mono.Android")] object target);

	[UnsafeAccessor (UnsafeAccessorKind.StaticMethod, Name = "GetPendingBridgeArgs")]
	static extern IntPtr GetPendingBridgeArgs (
		[UnsafeAccessorType ("Microsoft.Android.Runtime.JavaMarshalGCBridge, Mono.Android")] object target);

	[TestMethod]
	[DataRow ("net/dot/jni/test/GetThis")]
	[DataRow ("net/dot/jni/test/CallNonvirtualBase")]
	[DataRow ("net/dot/jni/test/CallNonvirtualDerived")]
	public unsafe void HandwrittenPeersUseAndroidGCReferenceContract (string jniTypeName)
	{
		using var peerType = new JniType (jniTypeName);
		using var listType = new JniType ("java/util/ArrayList");
		using var destination = new Java.Interop.JavaObject ();
		var peer = peerType.NewObject (peerType.GetConstructor ("()V"), null);
		JniObjectReference references = default;
		JniObjectReference retained = default;
		try {
			Assert.IsTrue (Java.Interop.Runtime.IsGCUserPeer (peer.Handle),
				$"{jniTypeName} should be recognized as an Android GC peer.");
			var field = peerType.GetInstanceField ("managedReferences", "Ljava/util/ArrayList;");
			references = JniEnvironment.InstanceFields.GetObjectField (peer, field);
			var size = listType.GetInstanceMethod ("size", "()I");
			Assert.AreEqual (0, JniEnvironment.InstanceMethods.CallIntMethod (references, size));

			var bridge = GetBridgeJni (null);
			Assert.IsTrue (bridge.AddReference (peer, destination.PeerReference, knownGCUserPeer: false));
			Assert.AreEqual (1, JniEnvironment.InstanceMethods.CallIntMethod (references, size));
			JniArgumentValue* arguments = stackalloc JniArgumentValue [1];
			arguments [0] = new JniArgumentValue (0);
			retained = JniEnvironment.InstanceMethods.CallObjectMethod (
				references, listType.GetInstanceMethod ("get", "(I)Ljava/lang/Object;"), arguments);
			Assert.IsTrue (JniEnvironment.Types.IsSameObject (destination.PeerReference, retained));

			bridge.ClearReferences (peer);
			Assert.AreEqual (0, JniEnvironment.InstanceMethods.CallIntMethod (references, size));
		} finally {
			JniObjectReference.Dispose (ref retained);
			JniObjectReference.Dispose (ref references);
			JniObjectReference.Dispose (ref peer);
		}
	}

	[TestMethod]
	public void OrdinaryJavaObjectsAreNotGCUserPeers ()
	{
		using var value = new Java.Interop.JavaObject ();
		Assert.IsFalse (Java.Interop.Runtime.IsGCUserPeer (value.PeerReference.Handle));
		Assert.IsFalse (Java.Interop.Runtime.IsGCUserPeer (IntPtr.Zero));
	}

	[TestMethod]
	public void NativeAndManagedBridgeLayoutsMatch ()
	{
		Assert.AreEqual (IntPtr.Size * 4, Marshal.SizeOf<MarkCrossReferencesArgs> ());
		Assert.AreEqual (IntPtr.Size * 2, Marshal.SizeOf<StronglyConnectedComponent> ());
		Assert.AreEqual (IntPtr.Size * 2, Marshal.SizeOf<ComponentCrossReference> ());

		Type handleContext = typeof (HandleContext);
		Assert.AreEqual (0, Marshal.OffsetOf (handleContext, "identityHashCode").ToInt32 ());
		Assert.AreEqual (IntPtr.Size, Marshal.OffsetOf (handleContext, "controlBlock").ToInt32 ());
		Assert.AreEqual (IntPtr.Size * 2, Marshal.SizeOf<HandleContext> ());

		Type controlBlock = typeof (HandleContext.JniObjectReferenceControlBlock);
		Assert.AreEqual (0, Marshal.OffsetOf (controlBlock, "handle").ToInt32 ());
		Assert.AreEqual (IntPtr.Size, Marshal.OffsetOf (controlBlock, "handle_type").ToInt32 ());
		Assert.AreEqual (IntPtr.Size + sizeof (int), Marshal.OffsetOf (controlBlock, "refs_added").ToInt32 ());
		Assert.AreEqual (IntPtr.Size + (2 * sizeof (int)), Marshal.SizeOf<HandleContext.JniObjectReferenceControlBlock> ());
	}

	[TestMethod]
	[DataRow (0ul, true, DisplayName = "EmptyComponentsRequireTemporaryPeers(ulong.MinValue,True)")]
	[DataRow (1ul, false, DisplayName = "EmptyComponentsRequireTemporaryPeers(1UL,False)")]
	[DataRow (2ul, false, DisplayName = "EmptyComponentsRequireTemporaryPeers(2UL,False)")]
	public void EmptyComponentsRequireTemporaryPeers (ulong count, bool expected)
	{
		Assert.AreEqual (expected, RequiresTemporaryPeer (null, new UIntPtr (count)));
	}

	[TestMethod]
	public void EmptyComponentsCanAddCrossReferenceWithoutLeakingLocalRefs ()
	{
		unsafe {
			// SAFETY: the test owns two initialized stack SCC records and one cross-reference with indices
			// 0 and 1; the descriptor reports those exact counts, and the accessor processes it synchronously before the stack unwinds.
			StronglyConnectedComponent* components = stackalloc StronglyConnectedComponent [2];
			components [0] = default;
			components [1] = default;
			ComponentCrossReference crossReference = new () { SourceGroupIndex = 0, DestinationGroupIndex = 1 };
			MarkCrossReferencesArgs args = new () {
				ComponentCount = 2,
				Components = components,
				CrossReferenceCount = 1,
				CrossReferences = &crossReference,
			};

			int localReferences = Java.Interop.Runtime.LocalReferenceCount;
			PrepareComponentsAndCrossReferences (null, &args);

			Assert.IsTrue (components [0].Count == 0,
				$"After adding a temporary-peer cross-reference, source SCC Count should be 0, but was {components [0].Count}.");
			Assert.IsTrue (components [1].Count == 0,
				$"After adding a temporary-peer cross-reference, destination SCC Count should be 0, but was {components [1].Count}.");
			int remainingLocalReferences = Java.Interop.Runtime.LocalReferenceCount;
			Assert.IsTrue (remainingLocalReferences == localReferences,
				$"Temporary peers should be released after the cross-reference is added: expected {localReferences} local references, but found {remainingLocalReferences}.");
		}
	}

	[TestMethod]
	[Timeout (60_000, CooperativeCancellation = true)]
	[TestCategory ("GCBridge")]
	[TestCategory ("ReferenceTracing")]
	public async Task ManagedPeerCycleSurvivesJavaRootAndIsCollectedAfterRelease ()
	{
		if (!Microsoft.Android.Runtime.RuntimeFeature.IsCoreClrRuntime && !Microsoft.Android.Runtime.RuntimeFeature.IsNativeAotRuntime)
			Assert.Inconclusive ("This test exercises the managed GC bridge.");

		WeakReference<BridgeCyclePeer> first;
		WeakReference<BridgeCyclePeer> second;
		using var listener = new ManagedObjectReferenceManagerTests.ReferenceListener (RuntimeEventSource.GlobalReferenceKeyword);
		var roots = new JavaObjectArray<Java.Lang.Object> (1);
		try {
			CreatePeerCycle (roots, out first, out second, out ulong firstHandle, out ulong secondHandle, out _, out _);
			await GCBridgeTestHelpers.WaitForBridgeRoundsAsync (testContext.CancellationToken);

			Assert.IsTrue (first.TryGetTarget (out _) && second.TryGetTarget (out _),
				"All peers in the managed cycle should survive while Java roots one peer.");
			AssertCycleSurvivedJavaCollection (listener, firstHandle, secondHandle);
		} finally {
			roots.Dispose ();
		}

		await GCBridgeTestHelpers.CollectUnreachablePeersAsync (testContext.CancellationToken);
		Assert.IsFalse (first.TryGetTarget (out _) || second.TryGetTarget (out _),
			"Both peers should be collected after releasing the Java root.");
	}

	[TestMethod]
	[Timeout (60_000, CooperativeCancellation = true)]
	[TestCategory ("GCBridge")]
	public void AcquisitionRequiresAnActuallyStrongJniReference ()
	{
		if (!Microsoft.Android.Runtime.RuntimeFeature.IsCoreClrRuntime)
			Assert.Inconclusive ("This acquisition path is specific to the CoreCLR bridge.");

		using var peer = new BridgeCyclePeer ();
		JniObjectReference weak = peer.PeerReference.NewWeakGlobalRef ();
		try {
			var mislabeledWeak = new JniObjectReference (weak.Handle, JniObjectReferenceType.Local);
			Assert.IsNull (JniEnvironment.Runtime.ValueManager.PeekPeer (mislabeledWeak));
			Assert.AreSame (peer, JniEnvironment.Runtime.ValueManager.PeekPeer (peer.PeerReference));
			Assert.AreSame (peer, JniEnvironment.Runtime.ValueManager.PeekPeer (weak));
		} finally {
			JniObjectReference.Dispose (ref weak);
		}
	}

	[TestMethod]
	[Timeout (60_000, CooperativeCancellation = true)]
	[TestCategory ("GCBridge")]
	[DoNotParallelize]
	public async Task StrongJniReferenceAcquiresPeerBeforeBridgePreparation ()
	{
		if (!Microsoft.Android.Runtime.RuntimeFeature.IsCoreClrRuntime)
			Assert.Inconclusive ("This acquisition path is specific to the CoreCLR bridge.");

		await GCBridgeTestHelpers.WaitForBridgeRoundsAsync (testContext.CancellationToken);
		var roots = new JavaObjectArray<Java.Lang.Object> (1);
		WeakReference<BridgeCyclePeer> first;
		WeakReference<BridgeCyclePeer> second;
		try {
			CreatePeerCycle (roots, out first, out second, out _, out _, out int firstIdentity, out int secondIdentity);
			using var started = new ManualResetEventSlim ();
			using var resume = new ManualResetEventSlim ();
			using var stopped = new ManualResetEventSlim ();
			using var pauseListener = new PausingBridgeListener (started, resume, stopped);
			using var referenceListener = new ManagedObjectReferenceManagerTests.ReferenceListener (RuntimeEventSource.GlobalReferenceKeyword);
			pauseListener.Arm ();

			try {
				GC.Collect (generation: 2, mode: GCCollectionMode.Forced, blocking: true);
				Assert.IsTrue (started.Wait (TimeSpan.FromSeconds (10)), "The bridge did not start.");
				ulong firstHandle = FindPendingPeerHandle (firstIdentity);
				ulong secondHandle = FindPendingPeerHandle (secondIdentity);

				Task<bool> lookup = Task.Run (() => {
					var peer = roots [0] as BridgeCyclePeer;
					return peer != null && ReferenceEquals (peer, peer.Next.Next);
				});
				bool completedBeforeBridge = await Task.WhenAny (lookup, Task.Delay (TimeSpan.FromSeconds (3), testContext.CancellationToken)) == lookup;
				if (completedBeforeBridge)
					roots [0] = null!;

				resume.Set ();
				bool originalPeerReturned = await lookup.WaitAsync (TimeSpan.FromSeconds (15), testContext.CancellationToken);
				Assert.IsTrue (stopped.Wait (TimeSpan.FromSeconds (10)), "The bridge did not complete.");
				Assert.IsTrue (completedBeforeBridge, "A lookup with a strong JNI reference waited for the pending bridge.");
				Assert.IsTrue (originalPeerReturned, "The lookup must return the original managed peer and its cycle.");
				Assert.IsTrue (first.TryGetTarget (out _) && second.TryGetTarget (out _),
					"The acquired cycle should survive this bridge round after its Java root is removed.");

				Assert.IsFalse (referenceListener.GetEvents ().Any (e =>
					e.Id == RuntimeEventSource.WeakGlobalReferenceCreatedEventId &&
					(Equals (e.Payload [0], firstHandle) || Equals (e.Payload [0], secondHandle)) &&
					Equals (e.Payload [7], (int)GCBridgeReferenceOperation.GlobalToWeak)),
					"The acquired SCC must not be weakened during this bridge round.");
			} finally {
				pauseListener.Disarm ();
				resume.Set ();
				if (started.IsSet)
					Assert.IsTrue (stopped.Wait (TimeSpan.FromSeconds (10)), "The bridge did not complete after being resumed.");
			}
		} finally {
			roots.Dispose ();
		}

		await GCBridgeTestHelpers.CollectUnreachablePeersAsync (testContext.CancellationToken);
		Assert.IsFalse (first.TryGetTarget (out _) || second.TryGetTarget (out _),
			"Acquisition must not keep the peers alive in later bridge rounds.");
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static void CreatePeerCycle (JavaObjectArray<Java.Lang.Object> roots,
		out WeakReference<BridgeCyclePeer> first, out WeakReference<BridgeCyclePeer> second,
		out ulong firstHandle, out ulong secondHandle, out int firstIdentity, out int secondIdentity)
	{
		var a = new BridgeCyclePeer ();
		var b = new BridgeCyclePeer ();
		a.Next = b;
		b.Next = a;
		roots [0] = a;
		first = new WeakReference<BridgeCyclePeer> (a, trackResurrection: true);
		second = new WeakReference<BridgeCyclePeer> (b, trackResurrection: true);
		firstHandle = unchecked((ulong)(nuint)a.PeerReference.Handle);
		secondHandle = unchecked((ulong)(nuint)b.PeerReference.Handle);
		firstIdentity = a.JniIdentityHashCode;
		secondIdentity = b.JniIdentityHashCode;
	}

	static unsafe ulong FindPendingPeerHandle (int identity)
	{
		// SAFETY: the test pauses the bridge worker before preparation and keeps it paused
		// throughout this read, so the CLR-owned descriptor and context arrays remain live.
		var args = (MarkCrossReferencesArgs*)GetPendingBridgeArgs (null!);
		Assert.IsTrue (args != null, "A bridge graph should already be published.");
		for (nuint i = 0; i < args->ComponentCount; i++) {
			StronglyConnectedComponent component = args->Components [i];
			for (nuint j = 0; j < component.Count; j++) {
				var context = (HandleContext*)component.Contexts [j];
				if (context->PeerIdentityHashCode == identity)
					return unchecked((ulong)(nuint)context->PeerReference.Handle);
			}
		}
		Assert.Fail ($"Peer 0x{identity:x} was not included in the pending bridge graph.");
		return 0;
	}

	sealed class PausingBridgeListener : EventListener
	{
		readonly ManualResetEventSlim started;
		readonly ManualResetEventSlim resume;
		readonly ManualResetEventSlim stopped;
		readonly Lock gate = new ();
		bool armed;

		public PausingBridgeListener (ManualResetEventSlim started, ManualResetEventSlim resume, ManualResetEventSlim stopped)
		{
			this.started = started;
			this.resume = resume;
			this.stopped = stopped;
			RuntimeEventSource.Initialize ();
			foreach (EventSource source in EventSource.GetSources ()) {
				if (source.Name == RuntimeEventSource.ProviderName)
					EnableEvents (source, EventLevel.Informational, RuntimeEventSource.GCBridgeKeyword);
			}
		}

		public void Arm ()
		{
			lock (gate)
				armed = true;
		}

		public void Disarm ()
		{
			lock (gate)
				armed = false;
		}

		protected override void OnEventWritten (EventWrittenEventArgs eventData)
		{
			if (eventData.EventId == RuntimeEventSource.GCBridgeStartEventId) {
				bool pause;
				lock (gate) {
					pause = armed;
					armed = false;
					if (pause)
						started.Set ();
				}
				if (pause)
					resume.Wait ();
			} else if (eventData.EventId == RuntimeEventSource.GCBridgeStopEventId && started.IsSet) {
				stopped.Set ();
			}
		}
	}

	static void AssertCycleSurvivedJavaCollection (ManagedObjectReferenceManagerTests.ReferenceListener listener, params ulong [] globalHandles)
	{
		var events = listener.GetEvents ();
		var weakHandles = events
			.Where (e => e.Id == RuntimeEventSource.WeakGlobalReferenceCreatedEventId &&
				Equals (e.Payload [7], (int)GCBridgeReferenceOperation.GlobalToWeak) &&
				globalHandles.Any (handle => Equals (e.Payload [0], handle)))
			.Select (e => e.Payload [1])
			.ToArray ();
		Assert.IsNotEmpty (weakHandles, "The Java-rooted cycle must participate in global-to-weak bridge processing.");

		var survivingHandles = events
			.Where (e => e.Id == RuntimeEventSource.GlobalReferenceCreatedEventId &&
				Equals (e.Payload [7], (int)GCBridgeReferenceOperation.WeakToGlobal) &&
				weakHandles.Contains (e.Payload [0]))
			.ToArray ();
		Assert.IsNotEmpty (survivingHandles, "Java-rooted peers must be promoted back to global references.");
	}

	sealed class BridgeCyclePeer : Java.Lang.Object
	{
		public BridgeCyclePeer Next { get; set; }
	}

	[TestMethod]
	public void BridgeCallbackIsUnmanagedEntryPoint ()
	{
		Type bridge = typeof (JavaMarshalGCBridge);
		MethodInfo method = bridge.GetMethod ("ProcessBridge", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException ("ProcessBridge was not found.");
		Assert.IsNotNull (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute> ());
		Assert.AreEqual (typeof (void), method.ReturnType);

		ParameterInfo[] parameters = method.GetParameters ();
		Assert.AreEqual (1, parameters.Length);
		Assert.AreEqual (typeof (MarkCrossReferencesArgs).MakePointerType (), parameters [0].ParameterType);
	}
}
