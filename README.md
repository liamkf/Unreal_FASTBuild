# Unreal_FASTBuild
Allows the usage of FASTBuild with Unreal Engine 4 with a few minor modifications. It supports Visual Studio 2015 and 2017 and the Windows 10 SDK, UE4 4.21.1 and FASTBuild v0.95, although you should be able to use this with previous versions of UE4 by looking at the history of the file.

To use, place the under Engine/Source/Programs/UnrealBuildTool/System/ and add to the UnrealBuildTool project or regenerate the projects. You can then call it from ExecuteActions() in ActionGraph.cs a similar manner to XGE, for 4.21.1 it will look something like it does in the gist here: https://gist.github.com/liamkf/9e8a660be117c85428054fe76dfd5eff

It requires FBuild.exe to be in your path or modifying FBuildExePathOverride to point to where your FBuild executable is.

One example of the diffs and how to use it was made available here https://github.com/liamkf/Unreal_FASTBuild/issues/3 by Fire. Likewise following the steps here https://github.com/ClxS/FASTBuild-UE4 but using this version of the FASTBuild.cs file should also work.

There is also a few posts here http://knownshippable.com/blog/2017/03/07/fastbuild-with-unreal-engine-4-setup/ which may help people get setup with UE4, FASTBuild, as well as setting up distribution and caching.
