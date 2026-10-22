// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;
using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class MessageTests
{
    private static ParentalDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ParentalDbContext>()
            .UseInMemoryDatabase("messages-" + Guid.NewGuid())
            .Options);

    private static EmailRenderer NewRenderer() =>
        new(Options.Create(new EmailOptions { LogoUrl = "https://parental.opcontrol.net/brand/email-lockup.png" }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = "https://parental.opcontrol.net"
            }).Build());

    private static EmailSender NewEmailSender(ParentalDbContext db) =>
        new(db, new EphemeralDataProtectionProvider(), NewRenderer(), Options.Create(new EmailOptions()), NullLogger<EmailSender>.Instance);

    private static AlertService NewAlerts(ParentalDbContext db) =>
        new(db, new DashboardEventBus(), new EphemeralDataProtectionProvider(), NewEmailSender(db), NewRenderer(),
            NullLogger<AlertService>.Instance);

    private static MessageService NewMessages(ParentalDbContext db) =>
        new(db, new NullHubContext(), new DashboardEventBus(), NewAlerts(db), NewEmailSender(db), NewRenderer(),
            NullLogger<MessageService>.Instance);

    private sealed record SeedData(Family Family, Child Child, ParentUser Parent, Device Device, Device SecondDevice);

    private static async Task<SeedData> SeedAsync(ParentalDbContext db, string name)
    {
        var family = new Family { Name = name };
        var child = new Child { FamilyId = family.Id, Name = name + " child" };
        var parent = new ParentUser
        {
            FamilyId = family.Id,
            DisplayName = name + " parent",
            UserName = name.ToLowerInvariant() + "@example.test",
            Email = name.ToLowerInvariant() + "@example.test"
        };
        var device = new Device { FamilyId = family.Id, ChildId = child.Id, Name = name + " PC", TimeZoneId = "UTC" };
        var second = new Device { FamilyId = family.Id, ChildId = child.Id, Name = name + " tablet", TimeZoneId = "UTC" };
        db.Families.Add(family);
        db.Children.Add(child);
        db.Users.Add(parent);
        db.Devices.AddRange(device, second);
        await db.SaveChangesAsync();
        return new SeedData(family, child, parent, device, second);
    }

    [Fact]
    public void Messaging_permission_is_part_of_all_and_is_enforced()
    {
        Assert.True(ParentPermissions.All.HasFlag(ParentPermissions.Messaging));
        Assert.Equal(256, (int)ParentPermissions.Messaging);

        var access = new FamilyAccess(Guid.NewGuid(), Guid.NewGuid(), "Parent", FamilyRole.Parent,
            ParentPermissions.None, false, FamilyStatus.Active);
        Assert.Throws<PermissionDeniedException>(() => access.Require(ParentPermissions.Messaging));

        var allowed = access with { Permissions = ParentPermissions.Messaging };
        allowed.Require(ParentPermissions.Messaging);
    }

    [Fact]
    public async Task Preset_text_is_localized_and_500_characters_is_the_cap()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Preset");
        var messages = NewMessages(db);

        var preset = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("", "dinner", false, false, null));
        Assert.Equal("Dinner's ready", preset.Text);
        Assert.Equal("dinner", preset.Preset);

        var longText = new string('a', 600);
        var capped = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest(longText, null, false, false, null));
        Assert.Equal(500, capped.Text.Length);
    }

    [Fact]
    public async Task Another_family_can_never_read_send_or_delete()
    {
        await using var db = NewDb();
        var a = await SeedAsync(db, "Alpha");
        var b = await SeedAsync(db, "Beta");
        var messages = NewMessages(db);

        var message = await messages.SendFromParentAsync(a.Family.Id, a.Parent.Id, a.Child.Id,
            new ParentMessageRequest("hello", null, false, false, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.GetThreadAsync(b.Family.Id, a.Child.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.SendFromParentAsync(
            b.Family.Id, b.Parent.Id, a.Child.Id, new ParentMessageRequest("intrusion", null, false, false, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.DeleteAsync(b.Family.Id, message.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.MarkDeliveredAsync(b.Device, message.Id));

        await messages.ClearThreadAsync(b.Family.Id, a.Child.Id);
        var thread = await messages.GetThreadAsync(a.Family.Id, a.Child.Id);
        Assert.Single(thread);
    }

    [Fact]
    public async Task Offline_queue_is_pulled_on_connect_and_targets_the_right_device()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Queue");
        var messages = NewMessages(db);

        var childTarget = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("all devices", null, false, false, null));
        var deviceTarget = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("tablet only", null, false, false, seed.SecondDevice.Id));

        var pendingFirst = await messages.GetPendingForDeviceAsync(seed.Device);
        Assert.Single(pendingFirst);
        Assert.Equal(childTarget.Id, pendingFirst[0].Id);

        var pendingSecond = await messages.GetPendingForDeviceAsync(seed.SecondDevice);
        Assert.Equal(2, pendingSecond.Count);
        Assert.Contains(pendingSecond, m => m.Id == childTarget.Id);
        Assert.Contains(pendingSecond, m => m.Id == deviceTarget.Id);

        await messages.MarkDeliveredAsync(seed.Device, childTarget.Id);
        var after = await messages.GetPendingForDeviceAsync(seed.Device);
        Assert.DoesNotContain(after, m => m.Id == childTarget.Id);
    }

    [Fact]
    public async Task Status_transitions_are_first_device_wins_and_record_the_device()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Status");
        var messages = NewMessages(db);
        var message = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("check this", null, false, true, null));

        Assert.Equal(MessageStatus.Sent, message.Status);
        var delivered = await messages.MarkDeliveredAsync(seed.Device, message.Id);
        Assert.Equal(MessageStatus.Delivered, delivered.Status);
        var seen = await messages.MarkSeenAsync(seed.SecondDevice, message.Id);
        Assert.Equal(MessageStatus.Seen, seen.Status);
        var acknowledged = await messages.MarkAcknowledgedAsync(seed.SecondDevice, message.Id);
        Assert.Equal(MessageStatus.Acknowledged, acknowledged.Status);
        Assert.NotNull(acknowledged.AcknowledgedUtc);

        var row = await db.ChildMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id);
        Assert.Equal(seed.Device.Id, row.DeliveredByDeviceId);
        Assert.Equal(seed.SecondDevice.Id, row.SeenByDeviceId);
        Assert.Equal(seed.SecondDevice.Id, row.AcknowledgedByDeviceId);
    }

    [Fact]
    public async Task Child_messages_are_rate_limited_after_ten_in_ten_minutes()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Rate");
        var messages = NewMessages(db);

        for (var i = 0; i < 10; i++)
            await messages.SendFromChildAsync(seed.Device, new DeviceMessageRequest("message " + i));

        var error = await Assert.ThrowsAsync<MessageRateLimitException>(
            () => messages.SendFromChildAsync(seed.Device, new DeviceMessageRequest("one too many")));
        Assert.InRange(error.RetryAfterSeconds, 1, 601);
        Assert.Equal(10, await db.ChildMessages.CountAsync(m => m.Direction == MessageDirection.ChildToParent));
    }

    [Fact]
    public async Task Parent_read_state_and_soft_delete_hide_child_messages()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Read");
        var messages = NewMessages(db);

        var message = await messages.SendFromChildAsync(seed.Device, new DeviceMessageRequest("hi parents"));
        Assert.Equal(1, await messages.UnreadCountAsync(seed.Family.Id));

        await messages.MarkParentReadAsync(seed.Family.Id, seed.Child.Id, seed.Parent.Id);
        Assert.Equal(0, await messages.UnreadCountAsync(seed.Family.Id));

        await messages.DeleteAsync(seed.Family.Id, message.Id);
        Assert.Empty(await messages.GetThreadAsync(seed.Family.Id, seed.Child.Id));
        Assert.Empty(await messages.GetThreadForDeviceAsync(seed.Device));
        Assert.Equal(0, await messages.UnreadCountAsync(seed.Family.Id));
    }

    [Fact]
    public async Task Parent_to_child_status_transitions_record_the_device_and_read()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "StatusNames");
        var messages = NewMessages(db);
        var message = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("check this", null, false, true, null));

        Assert.Equal(MessageUiStatus.Sent, MessageStatusRules.ForDisplay(message));
        var delivered = await messages.MarkDeliveredAsync(seed.Device, message.Id);
        Assert.Equal(MessageUiStatus.Delivered, MessageStatusRules.ForDisplay(delivered));
        Assert.Equal(seed.Device.Name, delivered.DeliveredBy);

        var seen = await messages.MarkSeenAsync(seed.SecondDevice, message.Id);
        Assert.Equal(MessageUiStatus.Read, MessageStatusRules.ForDisplay(seen));
        Assert.Equal(seed.SecondDevice.Name, seen.SeenBy);

        var ack = await messages.MarkAcknowledgedAsync(seed.SecondDevice, message.Id);
        Assert.Equal(MessageUiStatus.Read, MessageStatusRules.ForDisplay(ack));
        Assert.Equal(seed.SecondDevice.Name, ack.AcknowledgedBy);
    }

    [Fact]
    public async Task Child_to_parent_delivered_and_read_are_recorded_on_the_parent_dashboard()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "ParentStatus");
        var messages = NewMessages(db);
        var message = await messages.SendFromChildAsync(seed.Device, new DeviceMessageRequest("hello parents"));
        Assert.Equal(MessageUiStatus.Sent, MessageStatusRules.ForDisplay(message));

        Assert.Equal(1, await messages.MarkParentDeliveredAsync(seed.Family.Id, seed.Child.Id, seed.Parent.Id));
        var delivered = (await messages.GetThreadAsync(seed.Family.Id, seed.Child.Id)).Last();
        Assert.Equal(MessageUiStatus.Delivered, MessageStatusRules.ForDisplay(delivered));
        Assert.Equal(seed.Parent.DisplayName, delivered.ParentDeliveredBy);
        Assert.Null(delivered.ParentReadUtc);

        Assert.Equal(1, await messages.MarkParentReadAsync(seed.Family.Id, seed.Child.Id, seed.Parent.Id));
        var read = (await messages.GetThreadAsync(seed.Family.Id, seed.Child.Id)).Last();
        Assert.Equal(MessageUiStatus.Read, MessageStatusRules.ForDisplay(read));
        Assert.Equal(seed.Parent.DisplayName, read.ParentReadBy);
        Assert.NotNull(read.ParentReadUtc);
        Assert.Equal(0, await messages.UnreadCountAsync(seed.Family.Id));
    }

    [Fact]
    public void Outbox_round_trip_ignores_malformed_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "job11-outbox-" + Guid.NewGuid() + ".jsonl");
        try
        {
            MessageOutbox.Append(path, new MessageOutboxEntry("first", DateTime.UtcNow));
            MessageOutbox.Append(path, new MessageOutboxEntry("second", DateTime.UtcNow.AddSeconds(1)));
            File.AppendAllText(path, "not json" + Environment.NewLine);
            var entries = MessageOutbox.Read(path);
            Assert.Equal(2, entries.Count);
            Assert.Equal("first", entries[0].Text);
            Assert.Equal("second", entries[1].Text);

            MessageOutbox.Rewrite(path, [entries[1]]);
            var remaining = MessageOutbox.Read(path);
            Assert.Single(remaining);
            Assert.Equal("second", remaining[0].Text);

            MessageOutbox.Rewrite(path, []);
            Assert.Empty(MessageOutbox.Read(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Pending_and_failed_status_labels_are_distinct()
    {
        Assert.Equal("Messages.Status.Pending", MessageStatusRules.LabelKey(MessageUiStatus.Pending));
        Assert.Equal("Messages.Status.Failed", MessageStatusRules.LabelKey(MessageUiStatus.Failed));
        Assert.Equal("◷", MessageStatusRules.Icon(MessageUiStatus.Pending));
        Assert.Equal("!", MessageStatusRules.Icon(MessageUiStatus.Failed));
        Assert.NotEqual(MessageStatusRules.Icon(MessageUiStatus.Sent), MessageStatusRules.Icon(MessageUiStatus.Delivered));
    }

    [Fact]
    public async Task Child_side_cannot_ack_a_message_targeted_to_another_device()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Target");
        var messages = NewMessages(db);
        var message = await messages.SendFromParentAsync(seed.Family.Id, seed.Parent.Id, seed.Child.Id,
            new ParentMessageRequest("first device only", null, false, true, seed.Device.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.MarkAcknowledgedAsync(seed.SecondDevice, message.Id));
    }

    [Fact]
    public async Task Child_message_creates_an_alert_and_unread_badge_state()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, "Alert");
        var messages = NewMessages(db);
        await messages.SendFromChildAsync(seed.Device, new DeviceMessageRequest("dinner was great"));

        var alert = await db.Alerts.AsNoTracking().SingleAsync();
        Assert.Equal("child-message", alert.Type);
        Assert.Equal("Alert.ChildMessage.Title", alert.TitleKey);
        Assert.Contains("dinner was great", alert.ArgsJson);
    }

    private sealed class NullHubContext : IHubContext<ParentalHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups { get; } = new NullGroups();
    }

    private sealed class NullClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NullProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
