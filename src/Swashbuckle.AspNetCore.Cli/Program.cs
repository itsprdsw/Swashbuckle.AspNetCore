﻿using System;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Swagger;
using System.Linq;
using Swashbuckle.AspNetCore.StartupAttribute;

namespace Swashbuckle.AspNetCore.Cli
{
    public class Program
    {
        static int Main(string[] args)
        {
            // Helper to simplify command line parsing etc.
            var runner = new CommandRunner("dotnet swagger", "Swashbuckle (Swagger) Command Line Tools", Console.Out);

            // NOTE: The "dotnet swagger tofile" command does not serve the request directly. Instead, it invokes a corresponding
            // command (called _tofile) via "dotnet exec" so that the runtime configuration (*.runtimeconfig & *.deps.json) of the
            // provided startupassembly can be used instead of the tool's. This is neccessary to successfully load the
            // startupassembly and it's transitive dependencies. See https://github.com/dotnet/coreclr/issues/13277 for more.

            // > dotnet swagger tofile ...
            runner.SubCommand("tofile", "retrieves Swagger from a startup assembly, and writes to file ", c =>
            {
                c.Argument("startupassembly", "relative path to the application's startup assembly");
                c.Argument("swaggerdoc", "name of the swagger doc you want to retrieve, as configured in your startup class");
                c.Option("--output", "relative path where the Swagger will be output, defaults to stdout");
                c.Option("--host", "a specific host to include in the Swagger output");
                c.Option("--basepath", "a specific basePath to inlcude in the Swagger output");
                c.Option("--format", "overrides the format of the Swagger output, can be Indented or None");
                c.OnRun((namedArgs) =>
                {
                    var depsFile = namedArgs["startupassembly"].Replace(".dll", ".deps.json");
                    var runtimeConfig = namedArgs["startupassembly"].Replace(".dll", ".runtimeconfig.json");

                    var subProcess = Process.Start("dotnet", string.Format(
                        "exec --depsfile {0} --runtimeconfig {1} {2} _{3}", // note the underscore
                        depsFile,
                        runtimeConfig,
                        typeof(Program).GetTypeInfo().Assembly.Location,
                        string.Join(" ", args)
                    ));

                    subProcess.WaitForExit();
                    return subProcess.ExitCode;
                });
            });

            // > dotnet swagger _tofile ... (* should only be invoked via "dotnet exec")
            runner.SubCommand("_tofile", "", c =>
            {
                c.Argument("startupassembly", "");
                c.Argument("swaggerdoc", "");
                c.Option("--output", "");
                c.Option("--host", "");
                c.Option("--basepath", "");
                c.Option("--format", "");
                c.OnRun((namedArgs) =>
                {
                    // 1) Configure host with provided startupassembly
                    var startupAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                        Path.Combine(Directory.GetCurrentDirectory(), namedArgs["startupassembly"]));

                    Console.WriteLine($"Startup Assembly Name: {startupAssembly.FullName}");
                    startupAssembly.CustomAttributes.ToList().ForEach(customAttribute => Console.WriteLine(customAttribute.AttributeType.ToString()));

                    Console.WriteLine("Startup classes detected (marked by StartupAttribute):");
                    startupAssembly.GetClassesWithStartupAttribute().ToList().ForEach(type => Console.WriteLine("* " + type.FullName));
                    //startupAssembly.GetTypes().Where(type => type.GetCustomAttribute(typeof(StartupAttribute), true));
                    //startupAssembly.GetCustomAttribute(typeof(StartupBase).)

                    var host = WebHost.CreateDefaultBuilder()
                        .UseStartup(startupAssembly.FullName)
                        .Build();

                    // 2) Retrieve Swagger via configured provider
                    var swaggerProvider = host.Services.GetRequiredService<ISwaggerProvider>();
                    var swagger = swaggerProvider.GetSwagger(
                        namedArgs["swaggerdoc"],
                        namedArgs.ContainsKey("--host") ? namedArgs["--host"] : null,
                        namedArgs.ContainsKey("--basepath") ? namedArgs["--basepath"] : null,
                        null);

                    // 3) Serialize to specified output location or stdout
                    var outputPath = namedArgs.ContainsKey("--output")
                        ? Path.Combine(Directory.GetCurrentDirectory(), namedArgs["--output"])
                        : null;

                    using (var streamWriter = (outputPath != null ? File.CreateText(outputPath) : Console.Out))
                    {
                        var mvcOptionsAccessor = (IOptions<MvcJsonOptions>)host.Services.GetService(typeof(IOptions<MvcJsonOptions>));

                        if (namedArgs.ContainsKey("--format"))
                        {
                            // TODO: Should this handle case where mvcJsonOptions.Value == null?
                            mvcOptionsAccessor.Value.SerializerSettings.Formatting = ParseEnum<Newtonsoft.Json.Formatting>(namedArgs["--format"]);
                        }

                        var serializer = SwaggerSerializerFactory.Create(mvcOptionsAccessor);

                        serializer.Serialize(streamWriter, swagger);

                        if (!string.IsNullOrWhiteSpace(outputPath))
                        {
                            Console.WriteLine($"Swagger JSON succesfully written to {outputPath}");
                        }
                    }

                    return 0;
                });
            });

            return runner.Run(args);
        }

        /// <summary>
        /// Parses a given option value into a specified enumeration value
        /// </summary>
        /// <typeparam name="T">enum</typeparam>
        /// <param name="optionValue">Expects the string representation of a valid Enumeration value,
        /// anything else defaults to the Enumeration's default value</param>
        protected static T ParseEnum<T>(string optionValue) where T : struct, IConvertible
        {
            var isParsed = Enum.TryParse(optionValue, true, out T parsed);

            return isParsed ? parsed : default(T);
        }
    }
}
