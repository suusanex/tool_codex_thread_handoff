#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:package OpenAI@2.14.0
#:include src/HandoffTool.cs

return await CodexThreadHandoff.Cli.RunAsync(args);
