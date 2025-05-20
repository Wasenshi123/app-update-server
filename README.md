# App Update Server
This is a simple REST based server for hosting update files for any app. It has dedicated updater app here: https://github.com/Wasenshi123/app-updater

The server use .NET 6.0

## Update File
This server expects the most universal format for every platform, **tarball** (```*.tar.gz```).
You can use any tool available in your platform to put every files and folders into .tar archive, then compress into .gz, that is the basic of tarball.

## Path Rule
The server support multiple apps. So you must specify your app name. The base path for all update file will be inside ```./apps/{APPNAME}``` path.
And in the API, version checking will be called at : ```http://{url-to-this-server}/update/{APPNAME}/check```

### App Name Config
You can specify the dictionary for app names, using **appsettings.json** file. The format is ```{APPNAME}: {corresponding folder name}```
```
"AppNames": {
    "HemoCheckIn": "CheckIn",
    "HemoBox": "Box"
  }
 ```

## Versioning Rule
This server use default common versionning format which is 2-4 number separated by dot (```x.x```, ```x.x.x```, ```x.x.x.x```). The server will detect the version by reading at the end of the name of update file: append at the end of the name ```-x.x``` e.g. ```update-1.2.tar.gz``` ```publish-2.0.0.0.tar.gz```

### Version Checking
The server will treat any version-less file name as the latest, always. e.g (```publish.tar.gz``` will always be newer than ```publish-10.0.0.tar.gz```)
And for the same versioning file, the general rule is, **the most recent modified will be the latest.**

(According to this rule, multiple version-less file names will virtually be interpreted as the same version, and the newest file will win.)

So in short, you can choose to just put your update file as tarball file into the corresponding path, (without version), and everything will just work. The newest file will always be treated as latest.

## New Version (1.2.0)
The server now support pre-release version
