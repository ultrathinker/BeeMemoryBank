using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Platforms.Apple.Interop;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The Objective-C plumbing under the Mac notice with buttons, on a real Mac: a block of ours is called through the function pointer in
/// its header, a class made at run time answers messages, and the notification centre's delegate turns a response into an event and calls
/// the completion handler it was given. What needs the signed app and the person's permission (the system showing the notice, a button
/// coming back from it) cannot run here; the logic around it is <see cref="MacOsActionNotifierTests"/>.
/// </summary>
public sealed class MacOsNotificationInteropTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ObjectGetter(IntPtr self, IntPtr command);

    [MacOnlyFact]
    public void ABlockOfOurs_IsCalledThroughItsHeader_OnceWithItsArgument()
    {
        var seen = new System.Collections.Generic.List<IntPtr>();
        var block = ObjCBlocks.Create((IntPtr argument) => seen.Add(argument));

        ObjCBlocks.Invoke(block, 42);
        ObjCBlocks.Invoke(block, 43);

        seen.Should().Equal((IntPtr)42);
    }

    [MacOnlyFact]
    public void AClassMadeAtRunTime_AnswersTheMessagesSentToIt()
    {
        var answer = CF.CreateString("an answer");
        var cls = ObjCClass.Create("BmbInteropTestObject", null)!;
        cls.AddMethod("theAnswer", "@@:", new ObjectGetter((self, command) => answer));
        cls.Register();

        var instance = cls.NewInstance();

        ObjC.ToManaged(ObjC.Send(instance, ObjC.Sel("theAnswer"))).Should().Be("an answer");
    }

    [MacOnlyFact]
    public async Task TheNotificationDelegate_TurnsAResponseIntoAnEvent_AndCallsTheCompletionHandler()
    {
        // A stand-in for a UNNotificationResponse: one object that answers actionIdentifier, notification, request and identifier.
        var action = CF.CreateString("lan.no");
        var requestId = CF.CreateString("bmb.notice.test");
        var fake = ObjCClass.Create("BmbInteropFakeResponse", null)!;
        fake.AddMethod("actionIdentifier", "@@:", new ObjectGetter((self, command) => action));
        fake.AddMethod("identifier", "@@:", new ObjectGetter((self, command) => requestId));
        fake.AddMethod("notification", "@@:", new ObjectGetter((self, command) => self));
        fake.AddMethod("request", "@@:", new ObjectGetter((self, command) => self));
        fake.Register();
        var response = fake.NewInstance();

        var port = new UserNotificationsPort();
        var heard = new TaskCompletionSource<(string Request, string Action)>();
        port.ActionChosen += (request, chosen) => heard.TrySetResult((request, chosen));
        var delegateObject = UserNotificationsPort.MakeDelegate();
        delegateObject.Should().NotBe(IntPtr.Zero);
        ObjC.Send(delegateObject, ObjC.Sel("respondsToSelector:"), ObjC.Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"))
            .Should().NotBe(IntPtr.Zero);
        var completed = new TaskCompletionSource<bool>();
        var completion = ObjCBlocks.Create((IntPtr unused) => completed.TrySetResult(true));

        ObjC.Send(delegateObject, ObjC.Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"), IntPtr.Zero, response, completion);

        (await heard.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(("bmb.notice.test", "lan.no"));
        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the system waits for the completion handler");
    }

    [MacOnlyFact]
    public async Task ANoticeThatArrivesWhileTheAppIsInFront_IsPresentedAsABannerAndInTheList()
    {
        var delegateObject = UserNotificationsPort.MakeDelegate();
        var options = new TaskCompletionSource<IntPtr>();
        var completion = ObjCBlocks.Create((IntPtr presentation) => options.TrySetResult(presentation));

        ObjC.Send(delegateObject, ObjC.Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"), IntPtr.Zero, IntPtr.Zero, completion);

        (await options.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be((IntPtr)((1 << 3) | (1 << 4)), "UNNotificationPresentationOptionList | ...Banner");
    }
}
