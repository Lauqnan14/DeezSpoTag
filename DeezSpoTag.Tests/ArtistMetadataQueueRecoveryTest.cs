using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataQueueRecoveryTest
{
    public sealed record Item(long ArtistId, string Name, int Attempts = 0, DateTimeOffset? NextAttemptUtc = null, bool NeedsAttention = false);

    [Fact]
    public void RetainedRetryStateSurvivesQueueRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"artist-replay-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var type=typeof(ArtistMetadataCacheRefreshService).Assembly.GetType("DeezSpoTag.Web.Services.PersistentArtistQueueStore")!;
            var path=Path.Combine(root,"queue.json");
            var expected=new Item(7,"Artist",2,DateTimeOffset.UtcNow.AddMinutes(30),true);
            var items=new Dictionary<long,Item>{{7,expected}};
            type.GetMethod("PersistQueueSnapshot")!.MakeGenericMethod(typeof(Item)).Invoke(null,new object[] {items,new object(),path});
            var restored=new Dictionary<long,Item>();
            var channel=Channel.CreateUnbounded<Item>();
            type.GetMethod("RestoreAndReplaySnapshot")!.MakeGenericMethod(typeof(Item)).Invoke(null,new object[] {
                channel,restored,new object(),path,(Func<Item,long>)(item=>item.ArtistId),(Func<Item,bool>)(_=>true),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance});
            Assert.Equal(expected,Assert.Single(restored).Value);
            Assert.True(channel.Reader.TryRead(out var replay));
            Assert.Equal(expected,replay);
            Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        }
        finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async System.Threading.Tasks.Task ShutdownWhileImageWorkWaitsForRetryPreservesSnapshotAndAttempts()
    {
        var root=Path.Combine(Path.GetTempPath(),"artist-image-shutdown-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var type=typeof(LibraryArtistImageQueueService);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var service=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            var itemType=type.GetNestedType("QueueItem",BindingFlags.NonPublic)!;
            var item=Activator.CreateInstance(itemType,new object?[]{7L,"Artist",0,DateTimeOffset.UtcNow.AddHours(1),false,null})!;
            var dictionary=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(long),itemType))!;
            var channelFactory=System.Linq.Enumerable.Single(typeof(Channel).GetMethods(),m=>m.Name=="CreateUnbounded" && m.GetParameters().Length==0);
            var channel=channelFactory.MakeGenericMethod(itemType).Invoke(null,null)!;
            void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(service,value);
            Set("_dataRoot",root);Set("_queueItems",dictionary);Set("_queueLock",new object());Set("_channel",channel);
            Set("_logger",Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryArtistImageQueueService>.Instance);
            Assert.True((bool)type.GetMethod("TryEnqueue",flags)!.Invoke(service,new[]{item})!);
            var path=Path.Combine(root,"artist-image-queue.json");var before=File.ReadAllText(path);
            using var cancellation=new System.Threading.CancellationTokenSource();
            var worker=(System.Threading.Tasks.Task)type.GetMethod("ExecuteAsync",flags)!.Invoke(service,new object[]{cancellation.Token})!;
            var reader=channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            Assert.Equal(0,(int)reader.GetType().GetProperty("Count")!.GetValue(reader)!);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker);
            Assert.Equal(before,File.ReadAllText(path));
            Assert.Equal(1,((System.Collections.IDictionary)dictionary).Count);
            Assert.Equal(0,itemType.GetProperty("Attempts")!.GetValue(item));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void FailedCompletionSnapshotKeepsTheItemInMemory()
    {
        var root=Path.Combine(Path.GetTempPath(),"artist-complete-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"not a directory");
            var item=new Item(7,"Artist");var items=new Dictionary<long,Item>{{7,item}};
            var type=typeof(ArtistMetadataCacheRefreshService).Assembly.GetType("DeezSpoTag.Web.Services.PersistentArtistQueueStore")!;
            var error=Assert.Throws<TargetInvocationException>(()=>type.GetMethod("CompleteItem")!.MakeGenericMethod(typeof(Item))
                .Invoke(null,new object[] {item,items,new object(),Path.Combine(blocked,"queue.json"),(Func<Item,long>)(row=>row.ArtistId)}));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.Equal(item,Assert.Single(items).Value);
        }
        finally {Directory.Delete(root,true);}
    }

    [Fact]
    public void FailedChannelSubmissionDoesNotStrandAnArtistInTheDedupeMap()
    {
        var root = Path.Combine(Path.GetTempPath(), "artist-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = Channel.CreateUnbounded<Item>();
            channel.Writer.Complete();
            var items = new Dictionary<long, Item>();
            var type = typeof(ArtistMetadataCacheRefreshService).Assembly.GetType("DeezSpoTag.Web.Services.PersistentArtistQueueStore")!;
            var method = type.GetMethod("TryEnqueue")!.MakeGenericMethod(typeof(Item));
            var accepted = (bool)method.Invoke(null, new object[] { new Item(7, "Artist"), channel, items, new object(), Path.Combine(root,"queue.json"), (Func<Item,long>)(item => item.ArtistId) })!;
            Assert.False(accepted);
            Assert.Empty(items);
        }
        finally { Directory.Delete(root, true); }
    }
}
