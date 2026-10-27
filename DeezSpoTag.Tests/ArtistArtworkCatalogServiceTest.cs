using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistArtworkCatalogServiceTest
{
    [Fact]
    public async Task FillOnlyUpdateDoesNotOverwriteAConcurrentManualSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "artist-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "library.db");
        try
        {
            await using var connection = new SqliteConnection("Data Source=" + db);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE artist(id INTEGER PRIMARY KEY, preferred_image_path TEXT, preferred_background_path TEXT, updated_at TEXT); INSERT INTO artist(id) VALUES(1);";
            await command.ExecuteNonQueryAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Library"] = "Data Source=" + db }).Build();
            var repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
            await repository.UpdateArtistImagePathAsync(1, "manual.png", CancellationToken.None);
            Assert.False(await repository.TryFillArtistVisualPathAsync(1, background: false, "automatic.png", expectedPath: null, CancellationToken.None));
            command.CommandText = "SELECT preferred_image_path FROM artist WHERE id=1";
            Assert.Equal("manual.png", await command.ExecuteScalarAsync());
            Assert.True(await repository.TryFillArtistVisualPathAsync(1, background: true, "background.png", expectedPath: null, CancellationToken.None));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CorruptUrlCacheRecoversOnceAndPreservesOriginalBytesAndBlocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "artist-file-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Library"] = "Data Source=" + db }).Build();
            await new LibraryDbService(config, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
            await using (var connection = new SqliteConnection("Data Source=" + db))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO artist(id,name) VALUES(1,'Fixture Artist')";
                await command.ExecuteNonQueryAsync();
            }
            using var picture = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(160,160);
            var random = new Random(81);
            for (var y=0; y<160; y++) for(var x=0; x<160; x++)
                picture[x,y] = new((byte)random.Next(255), (byte)random.Next(255), (byte)random.Next(255));
            using var bytes = new MemoryStream();
            await picture.SaveAsync(bytes, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            var factory = new ImageFactory(bytes.ToArray());
            var service = (DeezSpoTag.Web.Services.ArtistArtworkCatalogService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DeezSpoTag.Web.Services.ArtistArtworkCatalogService));
            void Set(string field, object value) => service.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, value);
            Set("_repository", repository); Set("_httpClients", factory); Set("_logger", NullLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService>.Instance);
            Set("_cacheRoot", Path.Combine(root,"cache"));
            var url = "https://images.test/photo.png";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            var directory = Path.Combine(root,"cache","spotify","artists","1");
            Directory.CreateDirectory(directory);
            var corrupt = Path.Combine(directory, hash + ".png");
            await File.WriteAllTextAsync(corrupt, "invalid-original");
            var candidateType = service.GetType().GetNestedType("RemoteCandidate", System.Reflection.BindingFlags.NonPublic)!;
            var candidate = Activator.CreateInstance(candidateType, new object?[] { "spotify", "spotify:fixture", url, 160, 160 })!;
            var method = service.GetType().GetMethod("CacheCandidateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            async Task<string?> Cache() => await (Task<string?>)method.Invoke(service, new[] {(object)1L, candidate, CancellationToken.None})!;
            var recovered = await Cache();
            Assert.NotNull(recovered);
            Assert.NotEqual(corrupt, recovered);
            Assert.Equal("invalid-original", await File.ReadAllTextAsync(corrupt));
            var saved = await File.ReadAllBytesAsync(recovered!);
            Assert.Equal(recovered, await Cache());
            Assert.Equal(1, factory.Calls);
            Assert.Equal(saved, await File.ReadAllBytesAsync(recovered!));
            var row = Assert.Single(await repository.GetArtistArtworkCacheAsync(1));
            await repository.UpsertArtistArtworkCacheAsync(new ArtistArtworkCacheUpsertInput(1, "candidate", row.Identity, "spotify", url,
                row.LocalPath, row.ContentHash, row.Width, row.Height, "heuristic", null, false, true));
            await Cache();
            Assert.True(Assert.Single(await repository.GetArtistArtworkCacheAsync(1)).UserBlocked);
            var version=Path.Combine(directory,"new-version.png");
            picture[0,0]=new(255,0,0);await picture.SaveAsync(version,new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            var localMethod=service.GetType().GetMethod("CacheLocalAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            await (Task<string?>)localMethod.Invoke(service,new object?[]{1L,"spotify",row.Identity,version,"https://images.test/new.png",CancellationToken.None})!;
            var versions=await repository.GetArtistArtworkCacheAsync(1);
            Assert.Equal(2,versions.Count);
            Assert.Contains(versions,item=>item.LocalPath==row.LocalPath && item.UserBlocked);
            Assert.Equal(saved,await File.ReadAllBytesAsync(recovered!));
            var cache=new ArtistPageCacheRepository(config,NullLogger<ArtistPageCacheRepository>.Instance);
            var reject=service.GetType().GetMethod("RejectSourceArtworkAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            await (Task)reject.Invoke(null,new object[]{repository,cache,1L,"spotify","wrong-profile",
                new HashSet<string>{"https://images.test/new.png"},CancellationToken.None})!;
            var evidence=await cache.TryGetRequiredAsync("artist-artwork-rejected","1",CancellationToken.None);
            Assert.Contains(version,evidence!.PayloadJson,StringComparison.Ordinal);
            Assert.DoesNotContain(recovered!,evidence.PayloadJson,StringComparison.Ordinal);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }

    [Fact]
    public async Task LegacyDiskImagesParticipateInTextEligibilityAndByteDeduplication()
    {
        var root=Path.Combine(Path.GetTempPath(),"artist-legacy-images-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db=Path.Combine(root,"library.db");
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Library"]="Data Source="+db}).Build();
            await new LibraryDbService(config,NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
            var repository=new LibraryRepository(config,NullLogger<LibraryRepository>.Instance);
            await using(var connection=new SqliteConnection("Data Source="+db))
            {
                await connection.OpenAsync();await using var command=connection.CreateCommand();
                command.CommandText=@"INSERT INTO artist(id,name) VALUES(1,'Fixture Artist');
INSERT INTO folder(id,root_path,display_name,enabled) VALUES(1,'/fixture','fixture',1);
INSERT INTO album(id,artist_id,title) VALUES(1,1,'Fixture Album');
INSERT INTO track(id,album_id,title) VALUES(1,1,'Fixture Track');
INSERT INTO audio_file(id,path,folder_id) VALUES(1,'/fixture/track.flac',1);
INSERT INTO track_local(track_id,audio_file_id) VALUES(1,1);";
                await command.ExecuteNonQueryAsync();
            }
            var directory=Path.Combine(root,"cache","lastfm","artists","1");Directory.CreateDirectory(directory);
            var text=Path.Combine(directory,"catalogued-text.png");
            var free=Path.Combine(directory,"legacy-free.png");
            using(var image=new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(160,160))
            {
                for(var y=0;y<160;y++)for(var x=0;x<160;x++)image[x,y]=new((byte)(30+x),(byte)(20+y),100);
                await image.SaveAsync(free,new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                image[0,0]=new(0,0,0);
                await image.SaveAsync(text,new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            }
            File.Copy(free,Path.Combine(directory,"legacy-duplicate.png"));
            await repository.UpsertArtistArtworkCacheAsync(new ArtistArtworkCacheUpsertInput(1,"candidate","lastfm:text","lastfm",null,
                text,"catalogued-text",160,160,"heuristic",null,true,false));
            var service=(DeezSpoTag.Web.Services.ArtistArtworkCatalogService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DeezSpoTag.Web.Services.ArtistArtworkCatalogService));
            void Set(string name,object value)=>service.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(service,value);
            Set("_repository",repository);Set("_cacheRoot",Path.Combine(root,"cache"));Set("_logger",NullLogger<DeezSpoTag.Web.Services.ArtistArtworkCatalogService>.Instance);
            var result=await service.GetAsync(1,CancellationToken.None,excludeTextArt:true);
            Assert.NotEqual(text,Assert.Single(result.Visuals).Path);
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Fact]
    public void CorruptFilesCannotSuppressTextArtworkLastResort()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "corrupt but existing");
            var method = typeof(DeezSpoTag.Web.Services.ArtistArtworkCatalogService).GetMethod("IsUsableArtworkFile",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.False((bool)method!.Invoke(null, new object?[] {path})!);
        }
        finally {File.Delete(path);}
    }

    private sealed class ImageFactory(byte[] bytes) : System.Net.Http.IHttpClientFactory
    {
        public int Calls;
        public System.Net.Http.HttpClient CreateClient(string name) => new(new ImageHandler(this,bytes));
    }
    private sealed class ImageHandler(ImageFactory owner, byte[] bytes) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            owner.Calls++;
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content = new System.Net.Http.ByteArrayContent(bytes)};
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

}
