using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;

namespace Alostora.RestHouse;

public sealed record UpdateSeasonFolderRequest(string? FolderPath);
public sealed record ScanFolderRequest(string FolderPath);
public sealed record RegisterSelectedFilesRequest(string FolderPath, List<string> FilePaths);
public sealed record DetectedEpisodeLink(string VideoId, int EpisodeNumber, string? Title, bool Selected);
public sealed record LinkDetectedEpisodesRequest(List<DetectedEpisodeLink> Episodes);
public sealed record RelinkEpisodeRequest(string VideoId);
public sealed record PreparePlaybackRequest(int? AudioStreamIndex);

public partial class App : Application
{
 private WebApplication? _server;
 private async void OnStartup(object sender, StartupEventArgs e)
 {
  try {
   var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AlostoraRestHouse");
   Directory.CreateDirectory(root);
   var settings=AppSettings.Load(root);
   var library=settings.LibraryPath;
   var posters=settings.PostersPath;
   Directory.CreateDirectory(library);
   Directory.CreateDirectory(posters);
   var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
   var builder=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Directory.Exists(webRoot) ? webRoot : null });
   builder.WebHost.UseUrls("http://0.0.0.0:8096");
   var libraryService = new LibraryService(library,Path.Combine(root,"resthouse.db"));
   builder.Services.AddSingleton(libraryService);
   builder.Services.AddSingleton(new MediaConversionService(libraryService,settings.FfmpegPath,Path.Combine(root,"TranscodeCache")));
   builder.Services.AddSingleton(new WatchProgressService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new VideoMetadataService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new SeriesCatalogService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddHostedService(provider => new ScheduledBackupService(root,posters,provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledBackupService>>()));
   _server=builder.Build();
   static bool IsLanAddress(IPAddress? address)
   {
    if(address is null) return false;
    if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
    if(IPAddress.IsLoopback(address)) return true;
    if(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)
    {
     var b=address.GetAddressBytes();
     return b[0]==10 || (b[0]==172&&b[1]>=16&&b[1]<=31) || (b[0]==192&&b[1]==168) || (b[0]==169&&b[1]==254);
    }
    return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
   }
   _server.Use(async (context,next) =>
   {
    if(!IsLanAddress(context.Connection.RemoteIpAddress))
    {
     context.Response.StatusCode=StatusCodes.Status403Forbidden;
     await context.Response.WriteAsync("ALOSTORA REST HOUSE is available on the local network only.");
     return;
    }
    await next();
   });
   _server.UseDefaultFiles(); _server.UseStaticFiles();
   static bool IsLocal(HttpContext c)
   {
    var address = c.Connection.RemoteIpAddress;
    if(address is null) return false;
    if(address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    return IPAddress.IsLoopback(address);
   }
   _server.MapGet("/api/health",()=>Results.Ok(new{name="ALOSTORA REST HOUSE",status="running",version="0.3.0"}));
   _server.MapGet("/api/media-tools",(HttpContext c,MediaConversionService media)=>IsLocal(c)?Results.Ok(media.GetToolStatus()):Results.NotFound());
   _server.MapGet("/api/playback/{id}/tracks",async (string id,MediaConversionService media)=>Results.Ok(await media.ProbeAsync(id)));
   _server.MapPost("/api/playback/{id}/prepare",async (string id,PreparePlaybackRequest request,MediaConversionService media)=>
   {
    var result=await media.StartConversionAsync(id,request.AudioStreamIndex);
    return result.State=="failed"?Results.BadRequest(result):Results.Accepted("/api/playback/jobs/"+result.JobId,result);
   });
   _server.MapGet("/api/playback/jobs/{jobId}",(string jobId,MediaConversionService media)=>Results.Ok(media.GetJob(jobId)));
   _server.MapGet("/stream-cache/{jobId}.mp4",(string jobId,MediaConversionService media)=>
   {
    var path=media.ResolveCachedFile(jobId);
    return path is null?Results.NotFound():Results.File(path,"video/mp4",enableRangeProcessing:true);
   });

   _server.MapGet("/api/library",(LibraryService svc,VideoMetadataService meta,SeriesCatalogService catalog)=>
   {
    foreach(var seriesRow in catalog.ListSeries())
    {
     if(!string.IsNullOrWhiteSpace(seriesRow.FolderPath)&&Directory.Exists(seriesRow.FolderPath)) svc.EnsureFolderRegistered(seriesRow.FolderPath);
     foreach(var seasonRow in catalog.ListSeasons(seriesRow.SeriesId))
      if(!string.IsNullOrWhiteSpace(seasonRow.FolderPath)&&Directory.Exists(seasonRow.FolderPath)) svc.EnsureFolderRegistered(seasonRow.FolderPath);
    }
    var metadataById=meta.List().ToDictionary(x=>x.VideoId);
    var posterLookup=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    try
    {
     foreach(var posterFile in Directory.EnumerateFiles(posters,"*",SearchOption.AllDirectories))
     {
      if(Path.GetExtension(posterFile).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp")) continue;
      var key=Path.GetFileNameWithoutExtension(posterFile);
      if(!posterLookup.ContainsKey(key)) posterLookup[key]=posterFile;
     }
    }
    catch { }
    var result=svc.List().Select(v=>
    {
     metadataById.TryGetValue(v.Id,out var m);
     var posterPath=m?.PosterPath;
     if(string.IsNullOrWhiteSpace(posterPath))
     {
      var baseName=Path.GetFileNameWithoutExtension(v.FileName);
      if(posterLookup.TryGetValue(baseName,out var posterFile))
      {
       var relative=Path.GetRelativePath(posters,posterFile).Replace('\\','/');
       posterPath="/posters/"+string.Join("/",relative.Split('/').Select(Uri.EscapeDataString));
      }
     }
     return new{id=v.Id,title=m?.Title??v.Title,fileName=v.FileName,url=v.Url,sizeBytes=v.SizeBytes,description=m?.Description??"",year=m?.Year,genre=m?.Genre??"",kind=m?.Kind??"Movie",posterPath,director=m?.Director,cast=m?.Cast,rating=m?.Rating,runtimeMinutes=m?.RuntimeMinutes,releaseDate=m?.ReleaseDate,tags=m?.Tags};
    });
    return Results.Ok(result);
   });
   _server.MapGet("/api/series",(SeriesCatalogService series,LibraryService files,VideoMetadataService meta)=>
   {
    foreach(var seriesRow in series.ListSeries())
    {
     if(!string.IsNullOrWhiteSpace(seriesRow.FolderPath)&&Directory.Exists(seriesRow.FolderPath)) files.EnsureFolderRegistered(seriesRow.FolderPath);
     foreach(var seasonRow in series.ListSeasons(seriesRow.SeriesId))
      if(!string.IsNullOrWhiteSpace(seasonRow.FolderPath)&&Directory.Exists(seasonRow.FolderPath)) files.EnsureFolderRegistered(seasonRow.FolderPath);
    }
    return Results.Ok(series.GetCatalog(files,meta));
   });
   _server.MapGet("/api/admin/series",(HttpContext c,SeriesCatalogService series,LibraryService files)=>{ if(!IsLocal(c)) return Results.NotFound(); var rows=series.ListSeries(); foreach(var row in rows) if(!string.IsNullOrWhiteSpace(row.FolderPath)&&Directory.Exists(row.FolderPath)) files.EnsureFolderRegistered(row.FolderPath); return Results.Ok(rows); });
   _server.MapDelete("/api/admin/series/{seriesId}",(HttpContext c,string seriesId,SeriesCatalogService series)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(seriesId)||seriesId.Length>80) return Results.BadRequest(new{error="معرّف المسلسل غير صالح"});
    try { series.DeleteSeries(seriesId); return Results.Ok(new{deleted=true,message="تم حذف بيانات المسلسل ومواسمه وروابط حلقاته دون حذف ملفات الفيديو الأصلية"}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapDelete("/api/admin/seasons/{seasonId:long}",(HttpContext c,long seasonId,SeriesCatalogService series)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    try { series.DeleteSeason(seasonId); return Results.Ok(new{deleted=true,message="تم حذف الموسم وروابط حلقاته دون حذف ملفات الفيديو"}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/series",(HttpContext c,SaveSeriesRequest request,SeriesCatalogService series,LibraryService files)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.SeriesId)||request.SeriesId.Length>80||string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160||(request.Description?.Length??0)>2000||(request.Genre?.Length??0)>80||(request.Year is not null&&(request.Year<1888||request.Year>DateTime.UtcNow.Year+5))||(request.PosterPath?.Length??0)>200||(request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))) return Results.BadRequest(new{error="تحقق من بيانات المسلسل"});
    series.SaveSeries(request); if(!string.IsNullOrWhiteSpace(request.FolderPath)) files.RegisterFolder(request.FolderPath); return Results.Ok(new{saved=true});
   });
   _server.MapGet("/api/admin/seasons",(HttpContext c,string seriesId,SeriesCatalogService series,LibraryService files)=>{ if(!IsLocal(c)) return Results.NotFound(); var rows=series.ListSeasons(seriesId); foreach(var row in rows) if(!string.IsNullOrWhiteSpace(row.FolderPath)&&Directory.Exists(row.FolderPath)) files.RegisterFolder(row.FolderPath); return Results.Ok(rows); });
   _server.MapPost("/api/admin/seasons",(HttpContext c,SaveSeasonRequest request,SeriesCatalogService series,LibraryService files)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.SeriesId)||request.SeasonNumber<1||request.SeasonNumber>500||(request.Title?.Length??0)>120||(request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))) return Results.BadRequest(new{error="بيانات الموسم غير صحيحة أو مجلد الموسم غير موجود"});
    try { var id=series.AddSeason(request); if(!string.IsNullOrWhiteSpace(request.FolderPath)) files.RegisterFolder(request.FolderPath); return Results.Ok(new{seasonId=id}); } catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="المسلسل غير موجود أو رقم الموسم مكرر"}); }
   });
   _server.MapPost("/api/admin/seasons/{seasonId:long}/folder",(HttpContext c,long seasonId,UpdateSeasonFolderRequest request,SeriesCatalogService series)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if((request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))) return Results.BadRequest(new{error="مجلد الموسم غير موجود"});
    try { series.UpdateSeasonFolder(seasonId,request.FolderPath); return Results.Ok(new{saved=true}); }
    catch(KeyNotFoundException) { return Results.NotFound(new{error="الموسم غير موجود"}); }
   });
   _server.MapGet("/api/admin/seasons/{seasonId:long}/preview",(HttpContext c,long seasonId,SeriesCatalogService series,LibraryService files)=>{
    if(!IsLocal(c)) return Results.NotFound();
    var season=series.GetSeason(seasonId);
    if(season is null) return Results.NotFound(new{error="الموسم غير موجود"});
    if(string.IsNullOrWhiteSpace(season.FolderPath)||!Directory.Exists(season.FolderPath)) return Results.BadRequest(new{error="حدد مجلد الموسم واحفظ مساره أولاً"});
    files.EnsureFolderRegistered(season.FolderPath);
    var linked=series.ListEpisodes(seasonId);
    var linkedIds=linked.Select(x=>x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var usedNumbers=linked.Select(x=>x.EpisodeNumber).ToHashSet();
    var candidates=files.ListFolder(season.FolderPath).Select(file=>new {
     videoId=file.Id,fileName=file.FileName,title=file.Title,
     episodeNumber=ParseEpisodeNumber(file.FileName),
     isAlreadyLinked=linkedIds.Contains(file.Id),
     isNumberUsed=ParseEpisodeNumber(file.FileName) is int parsedNumber && usedNumbers.Contains(parsedNumber)
    }).ToArray();
    return Results.Ok(candidates);
   });
   _server.MapPost("/api/admin/seasons/{seasonId:long}/link-detected",(HttpContext c,long seasonId,LinkDetectedEpisodesRequest request,SeriesCatalogService series,LibraryService files)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(request.Episodes is null||request.Episodes.Count>1000) return Results.BadRequest(new{error="قائمة الحلقات غير صالحة"});
    if(series.GetSeason(seasonId) is null) return Results.NotFound(new{error="الموسم غير موجود"});
    var existing=series.ListEpisodes(seasonId);
    var usedIds=existing.Select(x=>x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var usedNumbers=existing.Select(x=>x.EpisodeNumber).ToHashSet();
    var linkedCount=0; var skipped=0;
    foreach(var item in request.Episodes.Where(x=>x.Selected))
    {
     if(item.EpisodeNumber<1||item.EpisodeNumber>10000||string.IsNullOrWhiteSpace(item.VideoId)||files.Resolve(item.VideoId) is null||usedIds.Contains(item.VideoId)||usedNumbers.Contains(item.EpisodeNumber))
     { skipped++; continue; }
     try {
      series.AddEpisode(new SaveEpisodeRequest(seasonId,item.VideoId,item.EpisodeNumber,item.Title));
      usedIds.Add(item.VideoId); usedNumbers.Add(item.EpisodeNumber); linkedCount++;
     } catch(Microsoft.Data.Sqlite.SqliteException) { skipped++; }
    }
    return Results.Ok(new{linkedCount,skipped});
   });
   _server.MapGet("/api/admin/preview-folder",(HttpContext c,string folderPath,LibraryService files)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(folderPath)||folderPath.Length>1000||!Directory.Exists(folderPath)) return Results.BadRequest(new{error="حدد مجلداً موجوداً ويمكن الوصول إليه"});
    try { return Results.Ok(files.PreviewFolder(folderPath)); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/register-selected-files",(HttpContext c,RegisterSelectedFilesRequest request,LibraryService files)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.FolderPath)||request.FolderPath.Length>1000||!Directory.Exists(request.FolderPath)||request.FilePaths is null||request.FilePaths.Count>5000) return Results.BadRequest(new{error="بيانات المجلد أو قائمة الملفات غير صحيحة"});
    try { var count=files.RegisterSelectedFiles(request.FolderPath,request.FilePaths); return Results.Ok(new{registered=true,videoCount=count}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/scan-folder",(HttpContext c,ScanFolderRequest request,LibraryService files)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.FolderPath)||(request.FolderPath.Length>1000)||!Directory.Exists(request.FolderPath)) return Results.BadRequest(new{error="حدد مجلداً موجوداً ويمكن الوصول إليه"});
    try { var count=files.RegisterFolder(request.FolderPath); return Results.Ok(new{registered=true,videoCount=count}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/episodes",(HttpContext c,SaveEpisodeRequest request,LibraryService files,SeriesCatalogService series)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(request.SeasonId<1||request.EpisodeNumber<1||request.EpisodeNumber>10000||string.IsNullOrWhiteSpace(request.VideoId)||files.Resolve(request.VideoId) is null||(request.Title?.Length??0)>160) return Results.BadRequest(new{error="بيانات الحلقة غير صحيحة أو ملف الفيديو غير موجود"});
    try { var id=series.AddEpisode(request); return Results.Ok(new{episodeId=id}); } catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="الموسم غير موجود أو الملف مربوط بحلقة أخرى أو رقم الحلقة مكرر"}); }
   });
   _server.MapGet("/api/admin/missing-episodes",(HttpContext c,SeriesCatalogService series,LibraryService files)=>
    IsLocal(c)?Results.Ok(series.ListMissingEpisodes(files)):Results.NotFound());
   _server.MapPost("/api/admin/episodes/{episodeId:long}/relink",(HttpContext c,long episodeId,RelinkEpisodeRequest request,SeriesCatalogService series,LibraryService files,WatchProgressService progress)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.VideoId)||request.VideoId.Length!=24||!request.VideoId.All(Uri.IsHexDigit)||files.Resolve(request.VideoId) is null)
     return Results.BadRequest(new{error="ملف الفيديو الجديد غير موجود في المكتبة"});
    try { var oldVideoId=series.UpdateEpisodeVideo(episodeId,request.VideoId); progress.MoveVideoProgress(oldVideoId,request.VideoId); return Results.Ok(new{relinked=true,progressPreserved=true}); }
    catch(KeyNotFoundException) { return Results.NotFound(new{error="الحلقة غير موجودة"}); }
    catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="هذا الملف مربوط بحلقة أخرى بالفعل"}); }
   });
   _server.MapGet("/api/admin/metadata",(HttpContext c,VideoMetadataService meta)=>IsLocal(c)?Results.Ok(meta.List()):Results.NotFound());
   _server.MapPost("/api/admin/metadata",(HttpContext c,SaveVideoMetadataRequest request,LibraryService librarySvc,VideoMetadataService meta)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.VideoId)||request.VideoId.Length!=24||!request.VideoId.All(Uri.IsHexDigit)||librarySvc.Resolve(request.VideoId) is null) return Results.BadRequest(new{error="الفيديو غير موجود"});
    if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160||(request.Description?.Length??0)>2000||(request.Genre?.Length??0)>80||(request.Year is not null&&(request.Year<1888||request.Year>DateTime.UtcNow.Year+5))||(request.PosterPath?.Length??0)>200||(request.Director?.Length??0)>160||(request.Cast?.Length??0)>1000||(request.Tags?.Length??0)>500||(request.Rating is <0 or >10)||(request.RuntimeMinutes is <1 or >1000)||(request.ReleaseDate?.Length??0)>10) return Results.BadRequest(new{error="تحقق من البيانات المدخلة"});
    meta.Save(request); return Results.Ok(new{saved=true});
   });
   _server.MapPost("/api/admin/poster/{videoId}",async (HttpContext c,string videoId,LibraryService librarySvc,VideoMetadataService meta)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(videoId.Length!=24||!videoId.All(Uri.IsHexDigit)) return Results.BadRequest(new{error="معرّف الفيديو غير صالح"});
    var video=librarySvc.Resolve(videoId); if(video is null) return Results.NotFound(new{error="الفيديو غير موجود"});
    if(c.Request.ContentLength is > 5_242_880) return Results.StatusCode(413);
    var contentType=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
    var ext=contentType switch {"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null) return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP فقط"});
    await using var ms=new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
    if(ms.Length==0||ms.Length>5_242_880) return Results.BadRequest(new{error="حجم الصورة يجب ألا يتجاوز 5 ميجابايت"});
    var bytes=ms.ToArray();
    var valid=contentType switch {
     "image/jpeg"=>bytes.Length>=3&&bytes[0]==0xFF&&bytes[1]==0xD8&&bytes[2]==0xFF,
     "image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
     "image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",
     _=>false
    };
    if(!valid) return Results.BadRequest(new{error="محتوى الصورة لا يطابق صيغتها"});
    var fileName=videoId+"-"+Guid.NewGuid().ToString("N")+ext;
    var fullPath=Path.Combine(posters,fileName);
    await File.WriteAllBytesAsync(fullPath,bytes);
    var old=meta.Get(videoId);
    meta.Save(new SaveVideoMetadataRequest(videoId,old?.Title??(librarySvc.List().First(x=>x.Id==videoId).Title),old?.Description??"",old?.Year,old?.Genre??"",old?.Kind??"Movie","/posters/"+fileName,old?.Director,old?.Cast,old?.Rating,old?.RuntimeMinutes,old?.ReleaseDate,old?.Tags));
    return Results.Ok(new{posterPath="/posters/"+fileName});
   });
   _server.MapPost("/api/admin/series-poster/{seriesId}",async (HttpContext c,string seriesId,SeriesCatalogService series)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(seriesId)||seriesId.Length>80) return Results.BadRequest(new{error="معرّف المسلسل غير صالح"});
    var old=series.GetSeries(seriesId); if(old is null) return Results.NotFound(new{error="المسلسل غير موجود"});
    if(c.Request.ContentLength is > 5_242_880) return Results.StatusCode(413);
    var contentType=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
    var ext=contentType switch {"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null) return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP فقط"});
    await using var ms=new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
    if(ms.Length==0||ms.Length>5_242_880) return Results.BadRequest(new{error="حجم الصورة يجب ألا يتجاوز 5 ميجابايت"});
    var bytes=ms.ToArray();
    var valid=contentType switch {
     "image/jpeg"=>bytes.Length>=3&&bytes[0]==0xFF&&bytes[1]==0xD8&&bytes[2]==0xFF,
     "image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
     "image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",
     _=>false
    };
    if(!valid) return Results.BadRequest(new{error="محتوى الصورة لا يطابق صيغتها"});
    var fileName="series-"+Guid.NewGuid().ToString("N")+ext;
    var fullPath=Path.Combine(posters,fileName);
    await File.WriteAllBytesAsync(fullPath,bytes);
    try { series.SaveSeries(new SaveSeriesRequest(old.SeriesId,old.Title,old.Description,old.Year,old.Genre,"/posters/"+fileName,old.FolderPath)); }
    catch { File.Delete(fullPath); throw; }
    return Results.Ok(new{posterPath="/posters/"+fileName});
   });
   _server.MapGet("/posters/{**fileName}",(string fileName)=>{
    if(string.IsNullOrWhiteSpace(fileName)||fileName.Length>500) return Results.NotFound();
    var rootPath=Path.GetFullPath(posters).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
    var relative=fileName.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar);
    var full=Path.GetFullPath(Path.Combine(posters,relative));
    if(!full.StartsWith(rootPath,StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    if(!File.Exists(full)&&!fileName.Contains('/'))
    {
     var previousPosters=Path.Combine(root,"posters");
     var previousFile=Path.Combine(previousPosters,fileName);
     if(File.Exists(previousFile)) full=previousFile;
     else return Results.NotFound();
    }
    var type=Path.GetExtension(full).ToLowerInvariant() switch {".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",_=>null};
    return type is null?Results.NotFound():Results.File(full,type);
   });
   _server.MapPost("/api/admin/library/{id}/remove",(HttpContext c,string id,LibraryService librarySvc)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(id)||id.Length!=24||!Regex.IsMatch(id,"^[a-fA-F0-9]{24}$")) return Results.BadRequest(new{error="معرّف الفيديو غير صالح"});
    try { librarySvc.RemoveFromLibrary(id); return Results.Ok(new{removed=true,message="تمت إزالة الفيديو من المكتبة دون حذف الملف الأصلي"}); }
    catch(FileNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapDelete("/api/admin/library/{id}",(HttpContext c,string id,LibraryService librarySvc,VideoMetadataService metadata,WatchProgressService progress)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(id)||id.Length!=24||!id.All(Uri.IsHexDigit)) return Results.BadRequest(new{error="معرّف الفيديو غير صالح"});
    try
    {
     librarySvc.DeleteFile(id);
     metadata.Delete(id);
     progress.DeleteVideo(id);
     return Results.Ok(new{deleted=true,message="تم حذف ملف الفيديو من القرص وتنظيف بياناته وسجل مشاهدته. إذا كان مربوطاً بحلقة فستظهر كحلقة مفقودة ويمكن إعادة ربطها."});
    }
    catch(FileNotFoundException) { return Results.NotFound(new{error="ملف الفيديو غير موجود أو حُذف مسبقاً"}); }
    catch(Exception ex) { return Results.Problem("تعذر حذف ملف الفيديو: "+ex.Message); }
   });
   _server.MapGet("/api/progress/{viewerId}",(string viewerId,WatchProgressService db)=>viewerId.Length<=80?Results.Ok(db.Get(viewerId)):Results.BadRequest());
   _server.MapPost("/api/progress",(WatchProgressRequest request,WatchProgressService db,LibraryService svc)=>{if(string.IsNullOrWhiteSpace(request.ViewerId)||request.ViewerId.Length>80||string.IsNullOrWhiteSpace(request.VideoId)||svc.Resolve(request.VideoId) is null||!double.IsFinite(request.PositionSeconds)||!double.IsFinite(request.DurationSeconds)||request.PositionSeconds<0||request.DurationSeconds<=0||request.PositionSeconds>request.DurationSeconds+5||request.PositionSeconds>864000||request.DurationSeconds>864000)return Results.BadRequest();db.Save(request);return Results.Ok(new{saved=true});});
      _server.MapGet("/api/subtitles/{id}",(string id,LibraryService svc)=>svc.Resolve(id) is null?Results.NotFound():Results.Ok(svc.ListSubtitles(id)));
   _server.MapGet("/api/subtitles/{id}/{fileName}",(string id,string fileName,LibraryService svc)=>{
    var content=svc.ReadSubtitle(id,fileName);
    return content is null?Results.NotFound():Results.Text(content,"text/vtt; charset=utf-8");
   });
   _server.MapGet("/api/subtitles/{id}/embedded/{streamIndex:int}",async (string id,int streamIndex,MediaConversionService media)=>
   {
    var content=await media.ReadEmbeddedSubtitleAsync(id,streamIndex);
    return content is null?Results.NotFound():Results.Text(content,"text/vtt; charset=utf-8");
   });
   _server.MapGet("/media/{id}",(string id,LibraryService svc)=>{var item=svc.Resolve(id);return item is null?Results.NotFound():Results.File(item.FullPath,item.ContentType,enableRangeProcessing:true);});
   await _server.StartAsync(); new MainWindow(root,library,posters,settings.FfmpegPath).Show();
  } catch(Exception ex){MessageBox.Show("تعذر تشغيل السيرفر:\n"+ex.Message,"استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 private static int? ParseEpisodeNumber(string fileName)
 {
  var name=Path.GetFileNameWithoutExtension(fileName);
  foreach(var pair in new[]{('٠','0'),('١','1'),('٢','2'),('٣','3'),('٤','4'),('٥','5'),('٦','6'),('٧','7'),('٨','8'),('٩','9'),('۰','0'),('۱','1'),('۲','2'),('۳','3'),('۴','4'),('۵','5'),('۶','6'),('۷','7'),('۸','8'),('۹','9')})
   name=name.Replace(pair.Item1,pair.Item2);
  var patterns=new[]{
   @"(?i)S\d{1,3}\s*E(\d{1,4})",
   @"(?i)(?:^|[ ._-])(?:EP|EPISODE)[ ._-]?(\d{1,4})(?:$|[ ._-])",
   @"الحلقة[ ._-]?(\d{1,4})",
   @"(?i)(?:^|[ ._-])E(\d{1,4})(?:$|[ ._-])"
  };
  foreach(var pattern in patterns)
  {
   var match=Regex.Match(name,pattern);
   if(match.Success&&int.TryParse(match.Groups[1].Value,out var number)&&number>0&&number<=10000) return number;
  }
  return null;
 }

 private async void OnExit(object sender,ExitEventArgs e){if(_server is not null){await _server.StopAsync();await _server.DisposeAsync();}}
}