# Unreal_FASTBuild
Allows the usage of FASTBuild with Unreal Engine 4 with a few minor modifications. As written supports Visual Studio 2015 and the Windows 10 SDK.

Place under Engine/Source/Programs/UnrealBuildTool/System/ and either add to the UnrealBuildTool project or regenerate the projects.

It requires fbuild.exe to be in your path or modifying where the process is spawned to provide it with a full path.

Building UE4 with VS2015 and Windows 10 may require some other minor fixes to compile, we ran into https://answers.unrealengine.com/questions/310966/vs2015-toolchain-blocked-by-errors-c3646.html and are using the modified Core.Build.cs as mentioned in the answer.
