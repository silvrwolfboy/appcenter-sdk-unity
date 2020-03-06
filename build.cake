#addin nuget:?package=Cake.FileHelpers
#addin nuget:?package=Cake.AzureStorage
#addin nuget:?package=NuGet.Core
#addin nuget:?package=Cake.Xcode
#load "utility.cake"

using System;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Runtime.Versioning;
using NuGet;

// Native SDK versions
var AndroidSdkVersion = "3.0.1-1+f4bafd4b2";
var IosSdkVersion = "3.0.1-9+b0b0b83295631713675ce8c40147f2ca6115c60f";
var UwpSdkVersion = "2.6.4";

// URLs for downloading binaries.
/*
 * Read this: http://www.mono-project.com/docs/faq/security/.
 * On Windows,
 *     you have to do additional steps for SSL connection to download files.
 *     http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https
 *     By running mozroots and install part of Mozilla's root certificates can make it work.
 */

var SdkStorageUrl = "https://mobilecentersdkdev.blob.core.windows.net/sdk/";
var AndroidUrl = SdkStorageUrl + "AppCenter-SDK-Android-" + AndroidSdkVersion + ".zip";
var IosUrl = SdkStorageUrl + "AppCenter-SDK-Apple-" + IosSdkVersion + ".zip";

var AppCenterModules = new [] {
    new AppCenterModule("appcenter-release.aar", "AppCenter.framework", "Microsoft.AppCenter", "Core"),
    new AppCenterModule("appcenter-analytics-release.aar", "AppCenterAnalytics.framework", "Microsoft.AppCenter.Analytics", "Analytics"),
    new AppCenterModule("appcenter-distribute-release.aar", new[] { "AppCenterDistribute.framework", "AppCenterDistributeResources.bundle" }, "Microsoft.AppCenter.Distribute", "Distribute"),
    new AppCenterModule("appcenter-push-release.aar", "AppCenterPush.framework", "Microsoft.AppCenter.Push", "Push"),
    new AppCenterModule("appcenter-crashes-release.aar", "AppCenterCrashes.framework", "Microsoft.AppCenter.Crashes", "Crashes")
};

var ExternalUnityPackages = new [] {
    // From https://github.com/googlesamples/unity-jar-resolver#getting-started
    // Import the play-services-resolver-*.unitypackage into your plugin project <...> ensuring that you add the -gvh_disable option.
    // You must specify the -gvh_disable option in order for the Version Handler to work correctly!
    new ExternalUnityPackage("play-services-resolver-" + ExternalUnityPackage.VersionPlaceholder + ".unitypackage",
                             "1.2.135",
                             SdkStorageUrl + ExternalUnityPackage.NamePlaceholder,
                             "-gvh_disable")
};

// UWP IL2CPP dependencies.
var UwpIL2CPPDependencies = new [] {
    new NugetDependency("sqlite-net-pcl", "1.3.1", "UAP, Version=v10.0")
};
var UwpIL2CPPJsonUrl = SdkStorageUrl + "Newtonsoft.Json.dll";

// Unity requires a specific NDK version for building Android with IL2CPP.
// Download from a link here: https://developer.android.com/ndk/downloads/older_releases.html
// Unity 2017.3 requires NDK r13b.
// The destination for the NDK download.
var NdkFolder = "android_ndk";

// Task TARGET for build
var Target = Argument("target", Argument("t", "Default"));

// Available AppCenter modules.
// AppCenter module class definition.
class AppCenterModule
{
    public string AndroidModule { get; private set; }
    public string[] IosModules { get; private set; }
    public string DotNetModule { get; private set; }
    public string Moniker { get; private set; }
    public bool UWPHasNativeCode { get; private set; }
    public string[] NativeArchitectures { get; private set; }

    public AppCenterModule(string android, string ios, string dotnet, string moniker, bool hasNative = false) :
        this(android, new[] { ios }, dotnet, moniker, hasNative)
    {
    }

    public AppCenterModule(string android, string[] ios, string dotnet, string moniker, bool hasNative = false)
    {
        AndroidModule = android;
        IosModules = ios;
        DotNetModule = dotnet;
        Moniker = moniker;
        UWPHasNativeCode = hasNative;
        if (hasNative)
        {
            NativeArchitectures = new string[] {"x86", "x64", "arm"};
        }
    }
}

class ExternalUnityPackage
{
    public static string NamePlaceholder = "<name>";
    public static string VersionPlaceholder = "<version>";

    public string Name { get; private set; }
    public string Version { get; private set; }
    public string Url { get; private set; }
    public string AdditionalImportArgs { get; private set; }

    public ExternalUnityPackage(string name, string version, string url, string additionalImportArgs = "")
    {
        AdditionalImportArgs = additionalImportArgs;
        Version = version;
        Name = name.Replace(VersionPlaceholder, Version);
        Url = url.Replace(NamePlaceholder, Name).Replace(VersionPlaceholder, Version);
    }
}

class NugetDependency
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string Framework { get; set; }
    public bool IncludeDependencies { get; set; }

    public NugetDependency(string name, string version, string framework, bool includeDependencies = true)
    {
        Name = name;
        Version = version;
        Framework = framework;
        IncludeDependencies = includeDependencies;
    }
}

// Spec files can have up to one dependency.
class UnityPackage
{
    private string _packageName;
    private string _packageVersion;
    private List<string> _includePaths = new List<string>();

    public UnityPackage(string specFilePath)
    {
        var result = AddFilesFromSpec(specFilePath);
        if (!result)
        {
            throw new Exception("Failed to form unity package.");
        }
    }

    private bool AddFilesFromSpec(string specFilePath)
    {
        var needsCore = Statics.Context.XmlPeek(specFilePath, "package/@needsCore") == "true";
        if (needsCore)
        {
            var specFileDirectory = System.IO.Path.GetDirectoryName(specFilePath);
            if (!AddFilesFromSpec(specFileDirectory + "/AppCenter.unitypackagespec")) 
            {
                return false;
            }
        }
        _packageName = Statics.Context.XmlPeek(specFilePath, "package/@name");
        _packageVersion = Statics.Context.XmlPeek(specFilePath, "package/@version");
        if (_packageName == null || _packageVersion == null)
        {
            Statics.Context.Error("Invalid format for UnityPackageSpec file '" + specFilePath + "': missing package name or version");
            return false;
        }

        var xpathPrefix = "/package/include/file[";
        var xpathSuffix= "]/@path";

        string lastPath = Statics.Context.XmlPeek(specFilePath, xpathPrefix + "last()" + xpathSuffix);
        var currentIdx = 1;
        var currentPath =  Statics.Context.XmlPeek(specFilePath, xpathPrefix + currentIdx++ + xpathSuffix);

        if (currentPath != null)
        {
            _includePaths.Add(currentPath);
        }
        while (currentPath != lastPath)
        {
            currentPath =  Statics.Context.XmlPeek(specFilePath, xpathPrefix + currentIdx++ + xpathSuffix);
            _includePaths.Add(currentPath);
        }
        return true;
    }

    public void CreatePackage(string targetDirectory)
    {
        // From https://github.com/googlesamples/unity-jar-resolver#getting-started
        // Export your plugin <...> ensuring that you <...> Add the -gvh_disable option.
        // You must specify the -gvh_disable option in order for the Version Handler to work correctly!
        var args = "-gvh_disable -exportPackage ";
        foreach (var path in _includePaths)
        {
            args += " " + path;
        }
        var fullPackageName =  _packageName + "-v" + _packageVersion + ".unitypackage";
        args += " " + targetDirectory + "/" + fullPackageName;
        var result = ExecuteUnityCommand(args);
        if (result != 0)
        {
            Statics.Context.Error("Something went wrong while creating Unity package '" + fullPackageName + "'");
        }
    }

    public void CopyFiles(DirectoryPath targetDirectory)
    {
        foreach (var path in _includePaths)
        {
            if (Statics.Context.DirectoryExists(path))
            {
                Statics.Context.CopyDirectory(path, targetDirectory.Combine(path));
            }
            else
            {
                Statics.Context.CopyFile(path, targetDirectory.CombineWithFilePath(path));
            }
        }
    }
}

// Downloading Android binaries.
Task("Externals-Android")
    .Does(() =>
{
    CleanDirectory("./externals/android");

    // Download zip file.
    DownloadFile(AndroidUrl, "./externals/android/android.zip");
    Unzip("./externals/android/android.zip", "./externals/android/");

    // Copy files
    foreach (var module in AppCenterModules)
    {
        var files = GetFiles("./externals/android/*/" + module.AndroidModule);
        CopyFiles(files, "Assets/AppCenter/Plugins/Android/");
    }
}).OnError(HandleError);

// Downloading iOS binaries.
Task("Externals-Ios")
    .Does(() =>
{
    CleanDirectory("./externals/ios");

    // Download zip file containing AppCenter frameworks
    DownloadFile(IosUrl, "./externals/ios/ios.zip");
    Unzip("./externals/ios/ios.zip", "./externals/ios/");

    // Copy files
    foreach (var module in AppCenterModules)
    {
        foreach (var iosModule in module.IosModules)
        {
            var destinationFolder = "Assets/AppCenter/Plugins/iOS/" + module.Moniker + "/" + iosModule;
            DeleteDirectoryIfExists(destinationFolder);
            MoveDirectory("./externals/ios/AppCenter-SDK-Apple/iOS/" + iosModule, destinationFolder);
        }
    }
}).OnError(HandleError);

// Downloading UWP binaries.
Task ("Externals-Uwp")
    .Does (() => {
        var feedIdNugetEnv = Argument("NuGetFeedId", EnvironmentVariable("NUGET_FEED_ID"));
        var userNugetEnv = EnvironmentVariable("NUGET_USER");
        var passwordNugetEnv = Argument("NuGetPassword", EnvironmentVariable("NUGET_PASSWORD"));
        var usePublicFeed = (string.IsNullOrEmpty (feedIdNugetEnv) || string.IsNullOrEmpty (userNugetEnv) || string.IsNullOrEmpty (passwordNugetEnv));

        CleanDirectory ("externals/uwp");
        EnsureDirectoryExists ("Assets/AppCenter/Plugins/WSA/");
        foreach (var module in AppCenterModules) {
            if (module.Moniker == "Distribute") {
                Warning ("Skipping 'Distribute' for UWP.");
                continue;
            }
            if (module.Moniker == "Crashes") {
                Warning ("Skipping 'Crashes' for UWP.");
                continue;
            }
            Information ("Downloading " + module.DotNetModule + "...");
            // Download nuget package

            GetUwpPackage (module, usePublicFeed);
        }
    }).OnError (HandleError);

// Builds the ContentProvider for the Android package and puts it in the
// proper folder.
Task("BuildAndroidContentProvider").Does(()=>
{
    // Folder and script locations
    var appName = "AppCenterLoaderApp";
    var libraryName = "appcenter-loader";
    BuildAndroidLibrary(appName, libraryName);
    libraryName = "appcenter-push-delegate";
    BuildAndroidLibrary(appName, libraryName);

    // This is a workaround for NDK build making an error where it claims
    // it can't find the built .so library (which is built successfully).
    // This error is breaking the CI build on Windows. If you are seeing this, 
    // most likely we haven't found a fix and this is an NDK bug.
    if (!IsRunningOnWindows())
    {
        appName = "BreakpadSupport";
        BuildAndroidLibrary(appName, "", false);

        // The build fails with an error that libPuppetBreakpad.so is not found but it's generated properly.
        // Might be related to the fact the the path to generate the library is relative, in any case it's a false negative.
        Warning ("Ignoring BreakpadSupport build failure... It's ok. Unity complains that the .so is not found which is not true. It's a false negative.");
        if (!FileExists("AppCenterDemoApp/Assets/Plugins/Android/libPuppetBreakpad.so")) 
        {
            throw new Exception("Building breakpad library failed.");
        }
    }
}).OnError(HandleError);

void BuildAndroidLibrary(string appName, string libraryName, bool copyBinary = true) {
    var libraryFolder = System.IO.Path.Combine(appName, libraryName);
    var gradleScript = System.IO.Path.Combine(libraryFolder, "build.gradle");

    // Compile the library
    var gradleWrapper = System.IO.Path.Combine(appName, "gradlew");
    if (IsRunningOnWindows())
    {
        gradleWrapper += ".bat";
    }
    var fullArgs = "-b " + gradleScript + " assembleRelease";

    var result = StartProcess(gradleWrapper, fullArgs);

    if (copyBinary) {

        // We check the result of the build only here because we need to check build
        // result only in case we build native binary.
        // In another case, when we build breakpad, we don't check it
        // Due to the known issue that breakpad build always reports false failure.
        if (result != 0)
        {
            throw new Exception("Failed to build android native library. Gradle result code: " + result);
        }
        // Source and destination of generated aar
        var aarName = libraryName + "-release.aar";
        var aarSource = System.IO.Path.Combine(libraryFolder, "build/outputs/aar/" + aarName);
        var aarDestination = "Assets/AppCenter/Plugins/Android";

        // Delete the aar in case it already exists in the Assets folder
        var existingAar = System.IO.Path.Combine(aarDestination, aarName);
        if (FileExists(existingAar))
        {
            DeleteFile(existingAar);
        }

        // Move the .aar to Assets/AppCenter/Plugins/Android
        MoveFileToDirectory(aarSource, aarDestination);
    }
}

// Install Unity Editor for Windows
Task("Install-Unity-Windows").Does(() => {
    string unityDownloadUrl = EnvironmentVariable("EDITOR_URL_WIN_NEW");
    string il2cppSupportDownloadUrl = EnvironmentVariable("IL2CPP_SUPPORT_URL_NEW");
    // const string dotNetSupportDownloadUrl = @"https://netstorage.unity3d.com/unity/88933597c842/TargetSupportInstaller/UnitySetup-UWP-.NET-Support-for-Editor-2018.2.17f1.exe";

    Information("Downloading Unity Editor...");
    DownloadFile(unityDownloadUrl, "./UnitySetup64.exe");
    Information("Installing Unity Editor...");
    var result = StartProcess("./UnitySetup64.exe", " /S");
    if (result != 0)
    {
        throw new Exception("Failed to install Unity Editor");
    }

    Information("Downloading IL2CPP support...");
    DownloadFile(il2cppSupportDownloadUrl, "./UnityIl2CppSupport.exe");
    Information("Installing IL2CPP support...");
    result = StartProcess("./UnityIl2CppSupport.exe", " /S");
    if (result != 0)
    {
        throw new Exception("Failed to install IL2CPP support");
    }
}).OnError(HandleError);

// Downloading UWP IL2CPP dependencies.
Task ("Externals-Uwp-IL2CPP-Dependencies")
    .Does (() => {
        var targetPath = "Assets/AppCenter/Plugins/WSA/IL2CPP";
        EnsureDirectoryExists (targetPath);
        EnsureDirectoryExists (targetPath + "/ARM");
        EnsureDirectoryExists (targetPath + "/X86");
        EnsureDirectoryExists (targetPath + "/X64");

        // NuGet.Core support only v2.
        var packageSource = "https://www.nuget.org/api/v2/";
        var repository = PackageRepositoryFactory.Default.CreateRepository (packageSource);
        foreach (var i in UwpIL2CPPDependencies) {
            var frameworkName = new FrameworkName (i.Framework);
            var package = repository.FindPackage (i.Name, SemanticVersion.Parse (i.Version));
            IEnumerable<IPackage> dependencies;
            if (i.IncludeDependencies) {
                dependencies = GetNuGetDependencies (repository, frameworkName, package);
            } else {
                dependencies = new [] { package };
            }
            var fileSystem = new PhysicalFileSystem (Environment.CurrentDirectory);
            foreach (var depPackage in dependencies) {
                Information ("Extract NuGet package: " + depPackage);

                // Extract.
                var path = "externals/uwp/" + depPackage.Id;
                depPackage.ExtractContents (fileSystem, path);

                ExtractNuGetPackage (depPackage, frameworkName, path, targetPath);
            }
        }

        // Download patched Newtonsoft.Json library to avoid Unity issue.
        // Details: https://forum.unity3d.com/threads/332335/
        DownloadFile (UwpIL2CPPJsonUrl, targetPath + "/Newtonsoft.Json.dll");

        // Process UWP IL2CPP dependencies.
        Information ("Processing UWP IL2CPP dependencies. This could take a minute.");
        var result = ExecuteUnityCommand ("-executeMethod AppCenterPostBuild.ProcessUwpIl2CppDependencies");
        if (result != 0)
        {
            throw new Exception("Something went wrong while executing post build script. Unity result code: " + result);
        }
    }).OnError (HandleError);

// Download and install all external Unity packages required.
Task("Externals-Unity-Packages").Does(()=>
{
    var directoryPath = new DirectoryPath("externals/unity-packages");
    CleanDirectory(directoryPath);
    foreach (var package in ExternalUnityPackages)
    {
        var destination = directoryPath.CombineWithFilePath(package.Name);
        DownloadFile(package.Url, destination);
        Information("Importing package " + package.Name + ". This could take a minute.");
        var command = package.AdditionalImportArgs + " -importPackage " + destination.FullPath;
        var result = ExecuteUnityCommand(command);
        if (result != 0)
        {
            throw new Exception("Something went wrong while importing packages. Unity result code: " + result);
        }
    }
}).OnError(HandleError);

// Add App Center packages to demo app.
Task("AddPackagesToDemoApp")
    .IsDependentOn("CreatePackages")
    .Does(()=>
{
    var specFiles = GetFiles("UnityPackageSpecs/*.unitypackagespec");
    foreach (var spec in specFiles)
    {
        Information("Add files from " + spec);
        var package = new UnityPackage(spec.FullPath);
        package.CopyFiles("AppCenterDemoApp");
    }
}).OnError(HandleError);

// Remove package files from demo app.
Task("RemovePackagesFromDemoApp").Does(()=>
{
    DeleteDirectoryIfExists("AppCenterDemoApp/Assets/AppCenter");
    DeleteDirectoryIfExists("AppCenterDemoApp/Assets/Plugins");
}).OnError(HandleError);

// Create a common externals task depending on platform specific ones
// NOTE: It is important to execute Externals-Uwp-IL2CPP-Dependencies *last*
// or steps that run Unity commands might cause the *.meta files to be deleted!
// (Unity deletes meta data files when it is opened if the corresponding files are not on disk.)
Task("Externals")
    .IsDependentOn("Externals-Uwp")
    .IsDependentOn("Externals-Ios")
    .IsDependentOn("Externals-Android")
    .IsDependentOn("BuildAndroidContentProvider")
    .IsDependentOn("Externals-Uwp-IL2CPP-Dependencies")
    .IsDependentOn("Externals-Unity-Packages")
    .Does(()=>
{
    DeleteDirectoryIfExists("externals");
});

// Creates Unity packages corresponding to all ".unitypackagespec" files
// in "UnityPackageSpecs" folder.
Task("Package").Does(()=>
{
    // Remove AndroidManifest.xml
    var path = "Assets/Plugins/Android/appcenter/AndroidManifest.xml";
    if (System.IO.File.Exists(path))
    {
        DeleteFile(path);
    }

    // Store packages in a clean folder.
    const string outputDirectory = "output";
    CleanDirectory(outputDirectory);
    var specFiles = GetFiles("UnityPackageSpecs/*.unitypackagespec");
    foreach (var spec in specFiles)
    {
        var package = new UnityPackage(spec.FullPath);
        package.CreatePackage(outputDirectory);
    }
});

Task("PrepareAssets").IsDependentOn("Externals");

// Creates Unity packages corresponding to all ".unitypackagespec" files
// in "UnityPackageSpecs" folder (and downloads binaries)
Task("CreatePackages").IsDependentOn("PrepareAssets").IsDependentOn("Package");

// Builds the puppet applications and throws an exception on failure.
Task("BuildPuppetApps")
    .IsDependentOn("PrepareAssets")
    .Does(()=>
{
    BuildApps("Puppet");
}).OnError(HandleError);

// Builds the demo applications and throws an exception on failure.
Task("BuildDemoApps")
    .IsDependentOn("AddPackagesToDemoApp")
    .Does(()=>
{
    BuildApps("Demo", "AppCenterDemoApp");
}).OnError(HandleError);

// Downloads the NDK from the specified location.
Task("DownloadNdk")
    .Does(()=>
{
    var ndkUrl = EnvironmentVariable("ANDROID_NDK_URL");
    if (string.IsNullOrEmpty(ndkUrl))
    {
        throw new Exception("Ndk Url is empty string or null");
    }
    var zipDestination = Statics.TemporaryPrefix + "ndk.zip";

    // Download required NDK
    DownloadFile(ndkUrl, zipDestination);

    NdkFolder = EnvironmentVariable("ANDROID_HOME");
    // Something is buggy about the way Cake unzips, so use shell on mac
    if (IsRunningOnUnix())
    {
        //CleanDirectory(NdkFolder);
        var result = StartProcess("unzip", new ProcessSettings{ Arguments = $"{zipDestination} -d {NdkFolder}"});
        if (result != 0)
        {
            throw new Exception("Failed to unzip ndk.");
        }
    }
    else
    {
        Unzip(zipDestination, NdkFolder);
    }
    CleanDirectory(NdkFolder + "/ndk-bundle");
    MoveDirectory (NdkFolder + "/android-ndk-r16b", NdkFolder + "/ndk-bundle");
}).OnError(HandleError);

void GetUwpPackage (AppCenterModule module, bool usePublicFeed) {
    // Prepare destination
    var destination = "Assets/AppCenter/Plugins/WSA/" + module.Moniker + "/";
    EnsureDirectoryExists (destination);
    DeleteFiles (destination + "*.dll");
    DeleteFiles (destination + "*.winmd");
    var nupkgPath = "externals/uwp/";
    if (usePublicFeed) {
        var packageSource = "https://www.nuget.org/api/v2/";
        var repository = PackageRepositoryFactory.Default.CreateRepository (packageSource);

        var package = repository.FindPackage (module.DotNetModule, SemanticVersion.Parse (UwpSdkVersion));
        IEnumerable<IPackage> dependencies = new [] { package };
        var fileSystem = new PhysicalFileSystem (Environment.CurrentDirectory);
        foreach (var depPackage in dependencies) {
            Information ("Extract NuGet package: " + depPackage);
            // Extract.
            nupkgPath = "externals/uwp/" + depPackage.Id + "/";
            depPackage.ExtractContents (fileSystem, nupkgPath);

            // Move assemblies.
            ExtractNuGetPackage (depPackage, new FrameworkName ("UAP, Version=v10.0"), nupkgPath, destination);
            // Delete the package
            DeleteFiles (nupkgPath);
        }
    } else {
        nupkgPath = GetNuGetPackage (module.DotNetModule, UwpSdkVersion);
        var tempContentPath = "externals/uwp/" + module.Moniker + "/";
        DeleteDirectoryIfExists (tempContentPath);
        // Unzip into externals/uwp/
        Unzip (nupkgPath, tempContentPath);
        ExtractNuGetPackage (null, new FrameworkName ("UAP, Version=v10.0"), tempContentPath, destination);
        // Delete the package
        DeleteFiles (nupkgPath);
    }
}

void BuildApps(string type, string projectPath = ".")
{
    if (Statics.Context.IsRunningOnUnix())
    {
        VerifyIosAppsBuild(type, projectPath);
        VerifyAndroidAppsBuild(type, projectPath);
    }
    else
    {
        VerifyWindowsAppsBuild(type, projectPath);
    }
}

void VerifyIosAppsBuild(string type, string projectPath)
{
    VerifyAppsBuild(type, "ios", projectPath,
    // From Unity 2018.3 changelog: "iOS: Marked Mono scripting backend as deprecated."
    new string[] { /*"IosMono",*/ "IosIl2CPP" },
    outputDirectory =>
    {
        var directories = GetDirectories(outputDirectory + "/*/*.xcodeproj");
        if (directories.Count == 0)
        {
            throw new Exception("No ios projects found in directory '" + outputDirectory + "'");
        }
        var xcodeProjectPath = directories.Single();
        Statics.Context.Information("Attempting to build '" + xcodeProjectPath.FullPath + "'...");
        BuildXcodeProject(xcodeProjectPath.FullPath);
        Statics.Context.Information("Successfully built '" + xcodeProjectPath.FullPath + "'");
    });
}

void VerifyAndroidAppsBuild(string type, string projectPath)
{
    var extraArgs = "";
    if (DirectoryExists(NdkFolder))
    {
        var absoluteNdkFolder = Statics.Context.MakeAbsolute(Statics.Context.Directory(NdkFolder));
        extraArgs += "-NdkLocation \"" + absoluteNdkFolder + "\"";
    }
    VerifyAppsBuild(type, "android", projectPath,
    new string[] { /*"AndroidMono", */"AndroidIl2CPP" },
    outputDirectory =>
    {
        // Verify that an APK was generated.
        if (Statics.Context.GetFiles(outputDirectory + "/*.apk").Count == 0)
        {
            throw new Exception("No apk found in directory '" + outputDirectory + "'");
        }
        Statics.Context.Information("Found apk.");
    }, extraArgs);
}

void VerifyWindowsAppsBuild(string type, string projectPath)
{
    VerifyAppsBuild(type, "wsaplayer", projectPath,
    new string[] { "WsaIl2CPPD3D" },
    outputDirectory =>
    {
        Statics.Context.Information("Verifying app build in directory: " + outputDirectory);
        var slnFiles = GetFiles(outputDirectory + "/*/*.sln");
        if (slnFiles.Count() == 0)
        {
            throw new Exception("No .sln files found in the following directory and all it's subdirectories: " + outputDirectory);
        }
        if (slnFiles.Count() > 1)
        {
            throw new Exception(string.Format("Multiple .sln files found in directory {0}: {1}", outputDirectory, string.Join(", ", slnFiles)));
        }
        var solutionFilePath = slnFiles.Single();
        Statics.Context.Information("Attempting to build '" + solutionFilePath.ToString() + "'...");
        Statics.Context.MSBuild(solutionFilePath.ToString(), c => c
        .SetConfiguration("Master")
        .WithProperty("Platform", "x86")
        .SetVerbosity(Verbosity.Minimal)
        .SetMSBuildPlatform(MSBuildPlatform.x86));
        Statics.Context.Information("Successfully built '" + solutionFilePath.ToString() + "'");
    });
}

void VerifyAppsBuild(string type, string platformIdentifier, string projectPath, string[] buildTypes, Action<string> verificatonMethod, string extraArgs = "")
{
    var outputDirectory = GetBuildFolder(type, projectPath);
    var methodPrefix = "Build" + type + ".Build" + type + "Scene";
    foreach (var buildType in buildTypes)
    {
        // Remove all existing builds and create new build.
        Statics.Context.CleanDirectory(outputDirectory);
        ExecuteUnityMethod(methodPrefix + buildType + " " + extraArgs, platformIdentifier);
        verificatonMethod(outputDirectory);

        // Remove all remaining builds.
        Statics.Context.CleanDirectory(outputDirectory);
    }

    // Remove all remaining builds.
    Statics.Context.CleanDirectory(outputDirectory);
}

Task("PublishPackagesToStorage").Does(()=>
{
    // The environment variables below must be set for this task to succeed
    var apiKey = Argument("AzureStorageAccessKey", EnvironmentVariable("AZURE_STORAGE_ACCESS_KEY"));
    var accountName = EnvironmentVariable("AZURE_STORAGE_ACCOUNT");
    var corePackageVersion = XmlPeek(File("UnityPackageSpecs/AppCenter.unitypackagespec"), "package/@version");
    var zippedPackages = "AppCenter-SDK-Unity-" + corePackageVersion + ".zip";
    var files = GetFiles("output/*.unitypackage");
    Zip("./", zippedPackages, files);
    Information("Publishing packages to blob storage: " + zippedPackages);
    AzureStorage.UploadFileToBlob(new AzureStorageSettings
    {
        AccountName = accountName,
        ContainerName = "sdk",
        BlobName = zippedPackages,
        Key = apiKey,
        UseHttps = true
    }, zippedPackages);
    DeleteFiles(zippedPackages);
    foreach (var package in GetBuiltPackages())
    {
        Information("Publishing packages to blob storage: " + package);
        var fileName = System.IO.Path.GetFileNameWithoutExtension(package); // AppCenterAnalytics-v1.0.0
        var index = fileName.IndexOf('-'); // 18
        var fileNameLatest = fileName.Substring(0, index) + "Latest.unitypackage"; // AppCenterAnalyticsLatest.unitypackage
        AzureStorage.UploadFileToBlob(new AzureStorageSettings
        {
            AccountName = accountName,
            ContainerName = "sdk",
            BlobName = fileNameLatest,
            Key = apiKey,
            UseHttps = true
        }, package);
    }
}).OnError(HandleError);

Task("RegisterUnity").Does(()=>
{
    var serialNumber = Argument<string>("UnitySerialNumber");
    var username = Argument<string>("UnityUsername");
    var password = Argument<string>("UnityPassword");

    // This will produce an error, but that's okay because the project "noproject" is used so that the
    // root isn't opened by unity, which could potentially remove important .meta files.
    ExecuteUnityCommand($"-serial {serialNumber} -username {username} -password {password}", "noproject");
    var found = false;
    if (Statics.Context.IsRunningOnUnix())
    {
        found = GetFiles("/Library/Application Support/Unity/*.ulf").Count > 0;
    } else {
        var localAppDataUnityPath = System.IO.Path.Combine(EnvironmentVariable("LOCALAPPDATA"), @"VirtualStore\ProgramData\Unity\*.ulf");
        found = GetFiles(localAppDataUnityPath).Count + GetFiles(System.IO.Path.Combine(EnvironmentVariable("PROGRAMDATA"), @"Unity\*.ulf")).Count > 0;
    }
    if (!found)
    {
        throw new Exception("Failed to activate license.");
    }
}).OnError(HandleError);

Task("UnregisterUnity").Does(()=>
{
    var result = ExecuteUnityCommand("-returnLicense", null);
    if (result != 0)
    {
        throw new Exception("Something went wrong while returning Unity license.");
    }
}).OnError(HandleError);


// Default Task.
Task("Default").IsDependentOn("PrepareAssets");

// Clean up files/directories.
Task("clean")
    .IsDependentOn("RemoveTemporaries")
    .Does(() =>
{
    DeleteDirectoryIfExists("externals");
    DeleteDirectoryIfExists("output");
    CleanDirectories("./**/bin");
    CleanDirectories("./**/obj");
});

string GetNuGetPackage(string packageId, string packageVersion)
{
    var nugetUser = EnvironmentVariable("NUGET_USER");
    var nugetPassword = Argument("NuGetPassword", EnvironmentVariable("NUGET_PASSWORD"));
    var nugetFeedId = Argument("NuGetFeedId", EnvironmentVariable("NUGET_FEED_ID"));
    packageId = packageId.ToLower();

    var url = "https://msmobilecenter.pkgs.visualstudio.com/_packaging/";
    url += nugetFeedId + "/nuget/v3/flat2/" + packageId + "/" + packageVersion + "/" + packageId + "." + packageVersion + ".nupkg";

    // Get the NuGet package
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create (url);
    request.Headers["X-NuGet-ApiKey"] = nugetPassword;
    request.Credentials = new NetworkCredential(nugetUser, nugetPassword);
    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
    var responseString = String.Empty;
    var filename = packageId + "." + packageVersion +  ".nupkg";
    using (var fstream = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite))
    {
        response.GetResponseStream().CopyTo(fstream);
    }
    return filename;
}

void ExtractNuGetPackage (IPackage package, FrameworkName frameworkName, string tempContentPath, string destination) {
    if (package != null) {
        IEnumerable<IPackageAssemblyReference> assemblies;
        VersionUtility.TryGetCompatibleItems (frameworkName, package.AssemblyReferences, out assemblies);

        // Move assemblies.
        foreach (var i in assemblies) {
            if (!FileExists (destination + "/" + i.Name)) {
                MoveFile (tempContentPath + "/" + i.Path, destination + "/" + i.Name);
            }
        }

    }
    // Move native binaries.
    var contentPathSuffix = "lib/uap10.0/";
    var runtimesPath = tempContentPath + "/runtimes";
    if (DirectoryExists (runtimesPath)) {
        var oneArch = "x86";
        foreach (var runtime in GetDirectories (runtimesPath + "/win10-*")) {
            var arch = runtime.GetDirectoryName ().ToString ().Replace ("win10-", "").ToUpper ();
            oneArch = arch;
            var nativeFiles = GetFiles (runtime + "/native/*");
            var targetArchPath = destination + "/" + arch;
            EnsureDirectoryExists (targetArchPath);
            foreach (var nativeFile in nativeFiles) {
                if (!FileExists (targetArchPath + "/" + nativeFile.GetFilename ())) {
                    MoveFileToDirectory (nativeFile, targetArchPath);
                }
            }
        }
        contentPathSuffix = "runtimes/win10-" + oneArch + "/" + contentPathSuffix;
    }

    if (package == null) {
        var files = GetFiles (tempContentPath + contentPathSuffix + "*");
        MoveFiles (files, destination);
    }
}

IList<IPackage> GetNuGetDependencies(IPackageRepository repository, FrameworkName frameworkName, IPackage package)
{
    var dependencies = new List<IPackage>();
    GetNuGetDependencies(dependencies, repository, frameworkName, package);
    return dependencies;
}

void GetNuGetDependencies(IList<IPackage> dependencies, IPackageRepository repository, FrameworkName frameworkName, IPackage package)
{
    // Declaring this outside the method causes a parse error on Cake for Mac.
    string[] IgnoreNuGetDependencies = {
        "Microsoft.NETCore.UniversalWindowsPlatform",
        "NETStandard.Library"
    };

    dependencies.Add(package);
    foreach (var dependency in package.GetCompatiblePackageDependencies(frameworkName))
    {
        if (IgnoreNuGetDependencies.Contains(dependency.Id))
        {
            continue;
        }
        var subPackage = repository.ResolveDependency(dependency, false, true);
        if (!dependencies.Contains(subPackage))
        {
            GetNuGetDependencies(dependencies, repository, frameworkName, subPackage);
        }
    }
}

void BuildXcodeProject(string projectPath)
{
    var projectFolder = System.IO.Path.GetDirectoryName(projectPath);
    var buildOutputFolder =  System.IO.Path.Combine(projectFolder, "build");
    XCodeBuild(new XCodeBuildSettings {
        Project = projectPath,
        Scheme = "Unity-iPhone",
        Configuration = "Release",
        DerivedDataPath = buildOutputFolder
    });
}

RunTarget(Target);
