cd ..
del .\bin\release\*.nupkg
.\_CreateNewNuGetPackage\DoNotModify\nuget pack .\HedgeHog.Core.csproj -Prop Configuration=Release -Symbols -OutputDirectory ./bin/release
.\_CreateNewNuGetPackage\RunMeToUploadNuGetPackage.cmd