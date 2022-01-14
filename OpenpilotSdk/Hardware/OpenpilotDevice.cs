﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using FFMpegCore.Pipes;
using GeoCoordinatePortable;
using NetTopologySuite.IO;
using OpenpilotSdk.Git;
using OpenpilotSdk.OpenPilot;
using OpenpilotSdk.OpenPilot.Fork;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using OpenpilotSdk.Sftp;
using Renci.SshNet.Common;
using Serilog;

namespace OpenpilotSdk.Hardware
{
    public abstract class OpenpilotDevice
    {
        private const int SshPort = 8022;

        protected virtual string NotConnectedMessage { get; set; } = "No connection has been made to the openpilot device";

        public bool IsAuthenticated { get; protected set; } = false;
        protected readonly string TempDirectory = Path.Combine(AppContext.BaseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal), "tmp");
        public int Port { get; set; } = 8022;
        public IPAddress IpAddress { get; set; }
        protected SftpClient SftpClient;
        protected SshClient SshClient;

        public virtual string DeviceName { get; protected set; }
        public virtual string StorageDirectory { get; protected set; } = @"/data/media/0/realdata/";
        public virtual string RebootCommand { get; protected set; } = @"am start -a android.intent.action.REBOOT";
        public virtual string ShutdownCommand { get; protected set; } = @"am start -n android/com.android.internal.app.ShutdownActivity";
        public virtual string FlashCommand { get; protected set; } = @"cd /data/openpilot/panda/board && ./recover.sh";
        public virtual string InstallEmuCommand { get; protected set; } = @"cd /data/openpilot && echo 'y' | bash <(curl -fsSL install.emu.sh) && source /data/community/.bashrc";
        public virtual string GitCloneCommand { get; protected set; } = @"cd /data && rm -rf openpilot; git clone -b {1} --depth 1 --single-branch --progress --recurse-submodules --shallow-submodules {0} openpilot";

        public string WorkingDirectory
        {
            get
            {
                return SftpClient.WorkingDirectory;
            }
        }

        public async Task UploadFileAsync(string source, string destination)
        {
            await using (var fileStream = File.OpenRead(source))
            {
                await Task.Factory.FromAsync(
                    SftpClient.BeginUploadFile(fileStream, destination),
                    SftpClient.EndUploadFile);
            }
        }

        public async Task<SftpFileStream> OpenReadAsync(string path)
        {
            await ConnectAsync();

            return await SftpClient.OpenAsync(path, FileMode.Open, FileAccess.Read, CancellationToken.None);
        }

        public SftpFileStream OpenRead(string path)
        {
            Connect();
            
            return SftpClient.OpenRead(path);
        }
        
        public async Task<Bitmap> GetThumbnailAsync(Drive drive)
        {
            await ConnectAsync();

            var firstSegment = drive.Segments.FirstOrDefault();
            if (firstSegment != null)
            {
                return await GetThumbnailAsync(firstSegment);
            }

            return null;
        }

        public async Task ExportDriveAsync(string exportPath, Drive drive, Camera camera, bool combineSegments = false, IProgress<int> progress = null)
        {
            await ConnectAsync();

            if (!Directory.Exists(exportPath))
            {
                Directory.CreateDirectory(exportPath);
            }

            if (combineSegments)
            {
                bool fileWritten = false;
                var filePath = Path.Combine(exportPath, drive.ToString() +  (char)camera) + ".hevc";
                await using (var fileStream = File.Create(filePath))
                {
                    foreach (var segment in drive.Segments)
                    {
                        string videoPath;
                        switch (camera)
                        {
                            case Camera.Wide:
                                videoPath = segment.WideCamera?.FullName;
                                break;
                            case Camera.Driver:
                                videoPath = segment.DriverCamera?.FullName;
                                break;
                            default:
                                videoPath = segment.FrontCamera?.FullName;
                                break;
                        }

                        if (!string.IsNullOrWhiteSpace(videoPath))
                        {
                            using (var segmentFile = await SftpClient.OpenAsync(videoPath, FileMode.Open, FileAccess.Read, CancellationToken.None))
                            {
                                await segmentFile.CopyToAsync(fileStream);
                            }
                        }

                        if (progress != null)
                        {
                            progress.Report(segment.Index);
                        }
                    }

                    fileWritten = fileStream.Length > 0;
                }

                if (!fileWritten)
                {
                    File.Delete(filePath);
                }

            }
            else
            {
                var m3uFileName = drive.ToString() + (char)camera + ".m3u";
                var m3uList = new List<string>();

                var exportTasks =
                    drive.Segments.Select((segment) => ExportSegmentAsync(exportPath, segment, camera, progress));
                await Task.WhenAll(exportTasks);

                foreach (var exportTask in exportTasks)
                {
                    m3uList.Add(exportTask.Result);
                }
                
                if (m3uList.Count > 1)
                {
                    await using (var file = File.CreateText(Path.Combine(exportPath, m3uFileName)))
                    {
                        await file.WriteAsync(string.Join(Environment.NewLine, m3uList));
                    }
                }
            }
        }
        
        public void ExportDrive(string path, Drive drive, IProgress<int> progress = null)
        {
            Connect();

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var m3uFileName = drive.ToString() + ".m3u";
            var m3uList = new StringBuilder();

            foreach (var segment in drive.Segments)
            {
                m3uList.AppendLine(ExportSegment(path, segment));
            }

            using (var file = File.CreateText(Path.Combine(path, m3uFileName)))
            {
                file.WriteAsync(m3uList.ToString());
            }
        }

        public async Task<string> ExportSegmentAsync(string path, DriveSegment driveSegment, Camera camera,
            IProgress<int> progress = null)
        {
            var videoPath = "";
            switch (camera)
            {
                case Camera.Wide:
                    videoPath = driveSegment.WideCamera?.FullName;
                    break;
                case Camera.Driver:
                    videoPath = driveSegment.DriverCamera?.FullName;
                    break;
                default:
                    videoPath = driveSegment.FrontCamera?.FullName;
                    break;
            }

            string fileName = "";
            if (!string.IsNullOrWhiteSpace(videoPath))
            {
                var fileNameWithoutExtension = new DirectoryInfo(Path.GetDirectoryName(videoPath)).Name + (char)camera;
                fileName = fileNameWithoutExtension + ".mp4";
                var outputFilePath = Path.Combine(path, fileNameWithoutExtension + Path.GetExtension(videoPath));
                var convertedFilePath = Path.Combine(path, fileName);
                if (!File.Exists(convertedFilePath))
                {
                    if (!File.Exists(outputFilePath))
                    {
                        await using (var outputFile = File.Create(outputFilePath))
                        {
                            if (Directory.Exists(Path.GetDirectoryName(path)))
                            {
                                var connectionInfo = new ConnectionInfo(IpAddress.ToString(), Port,
                                    "comma",
                                    new PrivateKeyAuthenticationMethod("comma",
                                        new PrivateKeyFile(Path.Combine(
                                            AppContext.BaseDirectory ??
                                            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                            "opensshkey"))));

                                using (var sftpClient = new SftpClient(connectionInfo))
                                {
                                    await _maxConcurrentConnectionLock.WaitAsync();
                                    try
                                    {
                                        await sftpClient.ConnectAsync(CancellationToken.None);
                                    }
                                    finally
                                    {
                                        _maxConcurrentConnectionLock.Release();
                                    }

                                    await using (var stream = await sftpClient.OpenAsync(videoPath, FileMode.Open,
                                                     FileAccess.Read, CancellationToken.None))
                                    {
                                        await stream.CopyToAsync(outputFile);
                                    }
                                }
                            }
                        }
                    }

                    await FFMpegArguments.FromFileInput(outputFilePath, true,
                            options => options.WithFramerate(20))
                        .OutputToFile(Path.Combine(path, fileName), true, options => options.CopyChannel())
                        .ProcessAsynchronously();
                    File.Delete(outputFilePath);
                }
            }

            if (progress != null)
            {
                progress.Report(driveSegment.Index);
            }

            return fileName;
        }

        public string ExportSegment(string path, DriveSegment driveSegment)
        {
            Connect();

            var newFileName = new DirectoryInfo(Path.GetDirectoryName(driveSegment.FrontCamera.FullName)).Name +
                              Path.GetExtension(driveSegment.FrontCamera.FullName);

            var filePath = Path.Combine(path, newFileName);
            if (!File.Exists(filePath))
            {
                using (var outputFile = File.Create(Path.Combine(path, newFileName)))
                {
                    if (Directory.Exists(Path.GetDirectoryName(path)))
                    {
                        SftpClient.DownloadFile(driveSegment.FrontCamera.FullName, outputFile, obj => { });
                    }
                }
            }

            return newFileName;
        }

        public async Task<Bitmap> GetThumbnailAsync(DriveSegment driveSegment)
        {
            await ConnectAsync();

            Bitmap thumbnail = null;
            var drive = new DirectoryInfo(Path.GetDirectoryName(driveSegment.FrontCamera.FullName)).Name;
            var cachedThumbnail = Path.Combine(TempDirectory, drive + ".jpg");


            if (!File.Exists(cachedThumbnail))
            {
                bool quickVideo = driveSegment.FrontCameraQuick != null;
                var offset = 0;

                var videoFile = driveSegment.FrontCameraQuick ?? driveSegment.FrontCamera;
                var imageBuffer = quickVideo ? new byte[10000] : new byte[200000];

                await using (var sftpFileStream = SftpClient.OpenRead(videoFile.FullName))
                {
                    while (offset < imageBuffer.Length)
                    {
                        offset = await sftpFileStream.ReadAsync(imageBuffer, offset, imageBuffer.Length - offset);
                    }
                }

                //using (var sftpFileStream = SftpClient.OpenRead(driveSegment.FrontCamera.FullName))
                await using (var msInput = new MemoryStream(imageBuffer))
                await using (var msOutput = new MemoryStream())
                {
                    try
                    {
                        await FFMpegArguments
                            .FromPipeInput(new StreamPipeSource(msInput), options => options.WithFramerate(20).ForceFormat(quickVideo ? "mpegts" : "rawvideo"))
                            .OutputToPipe(new StreamPipeSink(msOutput), options =>
                                options.WithVideoCodec(VideoCodec.Png)
                                    .WithFrameOutputCount(1)
                                    .ForceFormat("rawvideo"))
                            .ProcessAsynchronously();
                    }
                    catch (FFMpegException)
                    {
                        System.Diagnostics.Debug.Print("FFMpeg pipe exception");
                    }

                    msOutput.Position = 0;
                    thumbnail = new Bitmap(msOutput);
                    thumbnail.Save(cachedThumbnail);
                }

            }

            return thumbnail ?? (Bitmap)Image.FromFile(cachedThumbnail);
        }

        public DriveSegment GetSegment(DateTime driveDate, int index)
        {
            Connect();

            var segmentFolder = Path.Combine(StorageDirectory, driveDate.ToString("yyyy-MM-dd--HH-mm-ss--" + index));
            var segmentFiles = SftpClient.GetFiles(segmentFolder);


            ISftpFile frontCamera = null;
            ISftpFile driverCamera = null;
            ISftpFile wideCamera = null;
            ISftpFile quickLog = null;
            ISftpFile rawLog = null;
            ISftpFile frontCameraQuick = null;

            foreach (var segmentFile in segmentFiles)
            {
                if (segmentFile.Name.Equals("fcamera.hevc", StringComparison.OrdinalIgnoreCase))
                {
                    frontCamera = segmentFile;
                }
                else if (segmentFile.Name.Equals("dcamera.hevc", StringComparison.OrdinalIgnoreCase))
                {
                    driverCamera = segmentFile;
                }
                else if (segmentFile.Name.Equals("ecamera.hevc", StringComparison.OrdinalIgnoreCase))
                {
                    wideCamera = segmentFile;
                }
                else if (segmentFile.Name.Equals("rlog.bz2", StringComparison.OrdinalIgnoreCase))
                {
                    rawLog = segmentFile;
                }
                else if (segmentFile.Name.Equals("qlog.bz2", StringComparison.OrdinalIgnoreCase))
                {
                    quickLog = segmentFile;
                }
                else if (segmentFile.Name.Equals("qcamera.ts", StringComparison.OrdinalIgnoreCase))
                {
                    frontCameraQuick = segmentFile;
                }
            }


            //
            //directoryItem.Name.Equals("rlog.bz2", StringComparison.OrdinalIgnoreCase))


            return new DriveSegment(index, frontCamera, quickLog, rawLog, driverCamera, frontCameraQuick,wideCamera);
        }

        public async Task<List<GpxWaypoint>> MapillaryExportAsync(Drive drive)
        {
            await ConnectAsync();

            var waypoints = new List<GpxWaypoint>();

            foreach (var driveSegment in drive.Segments.OrderBy(segment => segment.Index))
            {
                waypoints.AddRange(await OpenPilot.Logging.LogFile.GetWayPointsAsync(SftpClient.OpenRead(driveSegment.RawLog.FullName)));

            }

            List<GpxWaypoint> waypointsToExport = new List<GpxWaypoint>();
            if (waypoints.Count > 1)
            {
                var firstWaypoint = waypoints.First();
                waypointsToExport.Add(firstWaypoint);
                var geoCoordinate = new GeoCoordinate(firstWaypoint.Latitude, firstWaypoint.Longitude,
                    (double)firstWaypoint.ElevationInMeters);

                for (int i = 1; i < waypoints.Count; i++)
                {
                    var nextCoordinate = new GeoCoordinate(waypoints[i].Latitude, waypoints[i].Latitude,
                        (double)waypoints[i].ElevationInMeters);

                    var distance = geoCoordinate.GetDistanceTo(nextCoordinate);
                    if (distance >= 5)
                    {
                        waypointsToExport.Add(waypoints[i]);
                        geoCoordinate = nextCoordinate;
                    }
                }

                waypointsToExport.Add(waypoints[waypoints.Count - 1]);
            }

            var waypointTable = new ImmutableGpxWaypointTable(waypointsToExport.Take(100));
            var trackSegment = new GpxTrackSegment(waypointTable, null);
            var track = new GpxTrack(drive.ToString(), null, null, "openpilot", ImmutableArray<GpxWebLink>.Empty, null, null, null, ImmutableArray.Create(trackSegment));
            var gpxFile = new GpxFile();
            gpxFile.Tracks.Add(track);
            File.WriteAllText(@"D:\OpenPilot\testf\test.gpx", gpxFile.BuildString(new GpxWriterSettings()));

            return waypointsToExport;
        }

        public async Task<IEnumerable<Firmware>> GetFirmwareVersions(IProgress<int> progress = null)
        {
            await ConnectAsync();

            var firmwares = new List<Firmware>();

            var drives = GetDrives();

            foreach (var drive in drives)
            {
                foreach (var driveSegment in drive.Segments.OrderBy(segment => segment.Index))
                {
                    var firmware = await OpenPilot.Logging.LogFile.GetFirmware(SftpClient.OpenRead(driveSegment.QuickLog.FullName));

                    if (firmware != null && firmware.Any())
                    {
                        return firmware;
                    }
                }
            }


            return firmwares;
        }

        public async Task<GpxFile> GenerateGpxFileAsync(Drive drive, IProgress<int> progress = null)
        {
            await ConnectAsync();

            var waypoints = new List<GpxWaypoint>();

            foreach (var driveSegment in drive.Segments.OrderBy(segment => segment.Index))
            {
                waypoints.AddRange(await OpenPilot.Logging.LogFile.GetWayPointsAsync(SftpClient.OpenRead(driveSegment.RawLog.FullName)));

                if (progress != null)
                {
                    progress.Report(driveSegment.Index);
                }
            }

            var waypointTable = new ImmutableGpxWaypointTable(waypoints);
            var trackSegment = new GpxTrackSegment(waypointTable, null);
            var track = new GpxTrack(drive.ToString(), null, null, "openpilot", ImmutableArray<GpxWebLink>.Empty, null, null, null, ImmutableArray.Create(trackSegment));
            var gpxFile = new GpxFile();
            gpxFile.Tracks.Add(track);

            return gpxFile;
        }

        public IEnumerable<Drive> GetDrives()
        {
            Connect();

            IOrderedEnumerable<ISftpFile> directoryListing;
            try
            {
                directoryListing = SftpClient.ListDirectory(StorageDirectory)
                    .Where(dir => OpenPilot.Extensions.FolderRegex.IsMatch(dir.Name))
                    .OrderBy(dir =>
                    {
                        var matches = OpenPilot.Extensions.FolderRegex.Match(dir.Name);
                        var date = DateTime.Parse(matches.Groups[1].Value + " " +
                                                  matches.Groups[2].Value.Replace("-", ":"));
                        return date;
                    }).ThenBy(dir =>
                        {
                            var matches = OpenPilot.Extensions.FolderRegex.Match(dir.Name);
                            var index = int.Parse(matches.Groups[3].Value);
                            return index;
                        }
                    );
            }
            catch (SftpPathNotFoundException)
            {
                yield break;
            }

            DateTime? previousSegmentDate = null;
            DateTime driveDate = DateTime.Today;
            var segmentList = new List<DriveSegment>();

            foreach (var segmentFolder in directoryListing)
            {
                var matches = OpenPilot.Extensions.FolderRegex.Match(segmentFolder.Name);
                driveDate = DateTime.Parse(
                    matches.Groups[1].Value + " " + matches.Groups[2].Value.Replace("-", ":"));
                if (previousSegmentDate == null)
                {
                    previousSegmentDate = driveDate;
                }

                var segmentIndex = int.Parse(matches.Groups[3].Value);
                var driveSegment = GetSegment(driveDate, segmentIndex);

                if (previousSegmentDate == driveDate)
                {
                    segmentList.Add(driveSegment);
                }
                else
                {
                    yield return new Drive((DateTime)previousSegmentDate, segmentList);
                    previousSegmentDate = driveDate;
                    segmentList = new List<DriveSegment> { driveSegment };
                }
            }

            if (segmentList.Count > 0)
            {
                yield return new Drive(driveDate, segmentList);
            }
        
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DeviceName) ? IpAddress.ToString() : IpAddress + " - " + DeviceName;
        }

        protected bool Equals(OpenpilotDevice other)
        {
            return Equals(IpAddress, other.IpAddress);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((OpenpilotDevice)obj);
        }

        public override int GetHashCode()
        {
            return IpAddress != null ? IpAddress.GetHashCode() : 0;
        }

        public static bool operator ==(OpenpilotDevice left, OpenpilotDevice right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(OpenpilotDevice left, OpenpilotDevice right)
        {
            return !Equals(left, right);
        }

        public async Task ChangeDirectoryAsync(string path)
        {
            await ConnectAsync();

            SftpClient.ChangeDirectory(path);
        }

        public void ChangeDirectory(string path)
        {
            Connect();

            SftpClient.ChangeDirectory(path);
        }

        public async Task<IEnumerable<ISftpFile>> EnumerateFileSystemEntriesAsync(string path = "/")
        {
            await ConnectAsync();

            var fileSystemEntries = SftpClient.EnumerateFileSystemEntries(path);
            return fileSystemEntries;
        }

        public IEnumerable<ISftpFile> EnumerateFileSystemEntries(string path = "/")
        {
            Connect();

            var fileSystemEntries = SftpClient.EnumerateFileSystemEntries(path);
            return fileSystemEntries;
        }

        public async Task<IEnumerable<ISftpFile>> EnumerateFilesAsync(string path = ".")
        {
            await ConnectAsync();

            var directoryListing = await SftpClient.ListDirectoryAsync(path, CancellationToken.None);
            return directoryListing;
        }

        public IEnumerable<ISftpFile> EnumerateFiles(string path = ".")
        {
            Connect();
            
            var directoryListing = SftpClient.ListDirectory(path);
            return directoryListing;
        }

        public static async IAsyncEnumerable<OpenpilotDevice> DiscoverAsync()
        {
            Log.Information("Scanning network for devices.");
            var pingRequests = new List<Task<OpenpilotDevice>>();
            HashSet<string> addressList = new HashSet<string>();

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                Log.Information("Found network interface: {interface}", networkInterface.Name);

                var unicastAddresses = networkInterface.GetIPProperties().UnicastAddresses;
                foreach (var unicastAddress in unicastAddresses)
                {
                    if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        Log.Information("Network is not an IPv4 network: {interface}", networkInterface.Name);
                        continue;
                    }

                    var subnetMask = unicastAddress.IPv4Mask.GetAddressBytes();
                    var ipAddress = unicastAddress.Address.GetAddressBytes();

                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(subnetMask);
                        Array.Reverse(ipAddress);
                    }
                    var endAddress = ~BitConverter.ToUInt32(subnetMask, 0);
                    if (endAddress > 1024)
                    {
                        Log.Information("Subnet contains more than 1024 addresses, skipping: {interface}.  End Address: {address}", networkInterface.Name, endAddress);
                        continue;
                    }
                    var localIp = BitConverter.ToUInt32(ipAddress, 0);

                    var startAddress = BitConverter.ToUInt32(ipAddress, 0) & BitConverter.ToUInt32(subnetMask, 0);

                    var startAddressBytes = BitConverter.GetBytes(startAddress + 1);
                    if (BitConverter.IsLittleEndian) Array.Reverse(startAddressBytes);
                    var startIPAddress = Convert.ToString(startAddressBytes[0]) + "." + Convert.ToString(startAddressBytes[1]) + "." +
                        Convert.ToString(startAddressBytes[2]) + "." + Convert.ToString(startAddressBytes[3]);

                    if (startIPAddress == "192.168.43.1")
                    {
                        if (!addressList.Contains(startIPAddress))
                        {
                            addressList.Add(startIPAddress);
                            pingRequests.Add(GetOpenpilotDevice(startIPAddress, SshPort));
                            continue;
                        }
                    }

                    var endAddressBytes = BitConverter.GetBytes(startAddress + endAddress);
                    if (BitConverter.IsLittleEndian) Array.Reverse(endAddressBytes);
                    var endIPAddress = Convert.ToString(endAddressBytes[0]) + "." + Convert.ToString(endAddressBytes[1]) + "." +
                                         Convert.ToString(endAddressBytes[2]) + "." + Convert.ToString(endAddressBytes[3]);

                    Serilog.Log.Information("Scanning IP range: {startAddress} - {endAddress}", startIPAddress, endIPAddress);

                    for (uint i = 1; i <= endAddress; i++)
                    {
                        var host = startAddress + i;
                        if (host == localIp) continue;

                        var hostBytes = BitConverter.GetBytes(host);
                        if (BitConverter.IsLittleEndian) Array.Reverse(hostBytes);

                        var hostAddress = Convert.ToString(hostBytes[0]) + "." + Convert.ToString(hostBytes[1]) + "." +
                                          Convert.ToString(hostBytes[2]) + "." + Convert.ToString(hostBytes[3]);

                        if (!addressList.Contains(hostAddress))
                        {
                            addressList.Add(hostAddress);
                            pingRequests.Add(GetOpenpilotDevice(hostAddress, SshPort));
                        }
                    }
                }
            }
            
            while (pingRequests.Count > 0)
            {
                var cts = new CancellationTokenSource();
                var timeout = Task.Delay(10000, cts.Token);
                var result = await Task.WhenAny(Task.WhenAny(pingRequests), timeout).ConfigureAwait(false);

                if (result != timeout)
                {
                    cts.Cancel();
                    var pingResult = await ((Task<Task<OpenpilotDevice>>)result).ConfigureAwait(false);
                    pingRequests.Remove(pingResult);
                    var openpilotDevice = await pingResult.ConfigureAwait(false);
                    if (openpilotDevice != null)
                    {
                        yield return openpilotDevice;
                    }
                }
                else
                {
                    Serilog.Log.Information("Timed Out");
                    break;
                }
            }
        }

        public static async Task<OpenpilotDevice> GetOpenpilotDevice(string address, int port, int timeout = 5000)
        {
            try
            {
                //SendPingAsync never completes, maybe skip this and jut try socket instead
                var socketConnected = false;
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {

                    var socketTask = socket.ConnectAsync(address, port);
                    await Task.WhenAny(Task.Delay(timeout), socketTask).ConfigureAwait(false);

                    socketConnected = socket.Connected;

                    if (socketConnected)
                    {
                        socket.Shutdown(SocketShutdown.Both);
                        socket.Disconnect(false);
                    }

                    socket.Close();
                }

                if (socketConnected)
                {
                    Serilog.Log.Information("Connected to {Address} on port {port}", address, port);

                    var privateKeys = new PrivateKeyFile[]
                    {
                        new PrivateKeyFile(Path.Combine(
                            AppContext.BaseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                            "opensshkey"))
                    };

                    bool foundDevice = false;
                    using (var client = new SshClient(address, port, "comma", privateKeys))
                    {
                        try
                        {
                            await client.ConnectAsync(CancellationToken.None);
                        }
                        catch (SshAuthenticationException)
                        {
                            var device = new UnknownDevice();
                            return device;
                        }

                        if (client.IsConnected)
                        {
                            foundDevice = true;
                        }
                    }

                    if (foundDevice)
                    {
                        try
                        {
                            using (var client = new SshClient(address, port, "root",
                            privateKeys))
                            {
                                await client.ConnectAsync(CancellationToken.None);
                                if (client.IsConnected)
                                {
                                    Serilog.Log.Information("Connected to Comma2 device at {Address} on port {port}", address, port);
                                    return new Comma2(IPAddress.Parse(address));
                                }
                                else
                                {
                                    Serilog.Log.Information("Connected to Comma3 device at {Address} on port {port}", address, port);
                                    return new Comma3(IPAddress.Parse(address));
                                }
                            }
                        }
                        catch (Exception)
                        {
                            Serilog.Log.Information("Connected to Comma3 device at {Address} on port {port}", address, port);
                            return (OpenpilotDevice)(new Comma3(IPAddress.Parse(address)));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Error("Failed to connect to {Address} with the following exception: {Exception}", address, e.Message);
            }

            return null;
        }

        public virtual async Task<ForkResult> ReinstallOpenpilotAsync(IProgress<InstallProgress> progress = null)
        {
            await ConnectAsync();

            using (var command = SshClient.CreateCommand("cd /data/openpilot && git remote get-url origin && git rev-parse --abbrev-ref HEAD"))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0;
                if (success)
                {
                    var results = result.Split("\n");
                    var username = results[0].Split("/")[3];
                    var branch = results[1];
                    if (progress == null)
                    {
                        return await InstallForkAsync(username, branch).ConfigureAwait(false);
                    }
                    else
                    {
                        return await InstallForkAsync(username, branch, progress).ConfigureAwait(false);
                    }
                }
                return new ForkResult(string.IsNullOrWhiteSpace(result) ? command.Error : result, false);
            }
            
        }

        public virtual async Task<bool> FlashPandaAsync()
        {
            await ConnectAsync();

            using (var command = SshClient.CreateCommand(FlashCommand))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0;
                return success;
            }
        }

        public virtual async Task<bool> InstallEmuAsync()
        {
            await ConnectAsync();

            using (var command = SshClient.CreateCommand(InstallEmuCommand))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0;
                return success;
            }
        }

        public virtual async Task<bool> ShutdownAsync()
        {
            await ConnectAsync();

            using (var command = SshClient.CreateCommand(ShutdownCommand))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0;
                return success;
            }
        }

        public virtual async Task<bool> RebootAsync()
        {
            await ConnectAsync();

            using (var command = SshClient.CreateCommand(RebootCommand))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0;
                return success;
            }
        }

        public virtual async Task<ForkResult> InstallForkAsync(string username, string branch, IProgress<InstallProgress> progress)
        {
            if (progress == null)
            {
                return await InstallForkAsync(username, branch).ConfigureAwait(false);
            }

            await ConnectAsync();

            var installCommand =
                string.Format(@"cd /data && rm -rf openpilot ; git clone -b {1} --depth 1 --single-branch --progress --recurse-submodules --shallow-submodules https://github.com/{0}/openpilot.git openpilot", username, branch);

            var progressRegex = new Regex(@"\d+(?=%)", RegexOptions.Compiled);
            int previousProgress = 0;
            string lastLineRead = "";

            using (var command = SshClient.CreateCommand(installCommand))
            {
                var asyncResult = command.BeginExecute();
                using (var streamReader = new StreamReader(command.ExtendedOutputStream))
                {
                    while (!asyncResult.IsCompleted)
                    {
                        var output = await streamReader.ReadLineAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            lastLineRead = output;
                            var match = progressRegex.Match(output);

                            if (!string.IsNullOrWhiteSpace(match.Value))
                            {
                                if (int.TryParse(match.Value, out int currentProgress))
                                {
                                    if (previousProgress != currentProgress)
                                    {
                                        previousProgress = currentProgress;
                                        var progressText = output.Substring(0, match.Index).Trim();
                                        progressText = progressText.Remove(progressText.Length - 1);
                                        progress.Report(new InstallProgress(currentProgress, progressText));
                                    }
                                }
                            }
                        }
                    }

                    var result = command.EndExecute(asyncResult);
                    var success = command.ExitStatus == 0;

                    if (success)
                    {
                        using (var rebootCommand = SshClient.CreateCommand(RebootCommand))
                        {
                            rebootCommand.BeginExecute();
                        }
                    }

                    return new ForkResult(string.IsNullOrWhiteSpace(result) ? lastLineRead : result,
                        success);
                }
            }
        }

        public virtual async Task<ForkResult> InstallForkAsync(string username, string branch)
        {
            await ConnectAsync();

            var installCommand =
                string.Format(@"cd /data && rm -rf openpilot; git clone -b {1} --depth 1 --single-branch --recurse-submodules --shallow-submodules https://github.com/{0}/openpilot.git openpilot", username, branch);

            using (var command = SshClient.CreateCommand(installCommand))
            {
                var result = await Task.Factory.FromAsync(command.BeginExecute(), command.EndExecute).ConfigureAwait(false);
                var success = command.ExitStatus == 0 || !(string.IsNullOrWhiteSpace(result) && !string.IsNullOrWhiteSpace(command.Error));

                if (success)
                {
                    using (var rebootCommand = SshClient.CreateCommand("reboot"))
                    {
                        rebootCommand.BeginExecute();
                    }
                }

                return new ForkResult(string.IsNullOrWhiteSpace(result) ? command.Error : result,
                    success);
            }
        }

        public async Task<ShellStream> GetShellStreamAsync()
        {
            await ConnectAsync();
            var client = SshClient.CreateShellStream(this.ToString(), 0, 0, 0, 0, 1024);
            return client;
        }

        public ShellStream GetShellStream()
        {
            Connect();
            var client = SshClient.CreateShellStream(this.ToString(), 0, 0, 0, 0, 1024);
            return client;
        }

        //More than 10 concurrent requests to SftpClient.ConnectAsync will throw an exception
        private readonly SemaphoreSlim _maxConcurrentConnectionLock = new SemaphoreSlim(10, 10);
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        public void Connect()
        {
            _connectionLock.Wait();
            try
            {
                if (SftpClient == null || !SftpClient.IsConnected)
                {
                    var connectionInfo = new ConnectionInfo(IpAddress.ToString(), Port,
                        "comma",
                        new PrivateKeyAuthenticationMethod("comma",
                            new PrivateKeyFile(Path.Combine(AppContext.BaseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "opensshkey"))));
                    SftpClient = new SftpClient(connectionInfo);
                    SshClient = new SshClient(connectionInfo);

                    SftpClient.Connect();
                    SshClient.Connect();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
            
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if(SftpClient != null && SftpClient.IsConnected)
            {
                return;
            }

            await _connectionLock.WaitAsync(cancellationToken);

            try
            {
                if (SftpClient == null || !SftpClient.IsConnected)
                {
                    var connectionInfo = new ConnectionInfo(IpAddress.ToString(), Port,
                        "comma",
                        new PrivateKeyAuthenticationMethod("comma",
                            new PrivateKeyFile(Path.Combine(AppContext.BaseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                                "opensshkey"))));
                    SftpClient = new SftpClient(connectionInfo);
                    SshClient = new SshClient(connectionInfo);

                    await _maxConcurrentConnectionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await SftpClient.ConnectAsync(cancellationToken);
                        SftpClient.ChangeDirectory("/data/openpilot/");

                        await SshClient.ConnectAsync(cancellationToken);
                    }
                    finally
                    {
                        _maxConcurrentConnectionLock.Release();
                    }
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }
}
