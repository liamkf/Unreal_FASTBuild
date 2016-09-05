# Unreal_FASTBuild
Allows the usage of FASTBuild with Unreal Engine 4 with a few minor modifications. As written it supports Visual Studio 2015 and the Windows 10 SDK.

To use, place the under Engine/Source/Programs/UnrealBuildTool/System/ and either add to the UnrealBuildTool project or regenerate the projects. You can then call it from ExecuteActions() in ActionGraph.cs a similar manner to XGE.

It requires fbuild.exe to be in your path or modifying where the process is spawned to provide it with a full path.

Building UE4 with VS2015 and Windows 10 may require some other minor fixes to compile, we ran into https://answers.unrealengine.com/questions/310966/vs2015-toolchain-blocked-by-errors-c3646.html and are using the modified Core.Build.cs as mentioned in the answer.

One example of the diffs and how to use it was made available here https://github.com/liamkf/Unreal_FASTBuild/issues/3 by Fire. Likewise following the steps here https://github.com/ClxS/FASTBuild-UE4 but using this version of the FASTBuild.cs file should also work!

Some friendly setup help posts should be coming soon! :)
