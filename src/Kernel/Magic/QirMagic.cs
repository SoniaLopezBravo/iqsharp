﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using LlvmBindings;
using LlvmBindings.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Quantum.IQSharp.AzureClient;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.QsCompiler;

namespace Microsoft.Quantum.IQSharp.Kernel
{
    /// <summary>
    ///     A magic command that can be used to generate QIR from a given
    ///     operation as an entry point.
    /// </summary>
    public class QirMagic : AbstractMagic
    {
        private const string ParameterNameOperationName = "__operationName__";
        private const string ParameterNameOutputPath = "output";

        /// <summary>
        ///     Constructs the magic command from DI services.
        /// </summary>
        public QirMagic(ISymbolResolver resolver, IEntryPointGenerator entryPointGenerator, ILogger<SimulateMagic> logger, IAzureClient azureClient) : base(
            "qir",
            new Microsoft.Jupyter.Core.Documentation
            {
                Summary = "Compiles a given Q# entry point to QIR, saving the resulting QIR to a given file.",
                Description = @"
                    This command takes the full name of a Q# entry point, and compiles the Q# from that entry point
                    into QIR. The resulting program is then executed, and the output of the program is displayed.

                    #### Required parameters

                    - Q# operation or function name. This must be the first parameter, and must be a valid Q# operation
                    or function name that has been defined either in the notebook or in a Q# file in the same folder.
                    - The file path for where to save the output QIR to, specified as `output=<file path>`.
                ".Dedent(),
                Examples = new []
                {
                    @"
                        Compiles a Q# program to QIR starting at the entry point defined as `operation MyOperation() : Result` and saves the result at MyProgram.ll:
                        ```
                        In []: %qir MyEntryPoint output=MyProgram.ll
                        Out[]: <There is no output printed to the notebook>
                        ```
                    ".Dedent()
                }
            }, logger)
        {
            this.EntryPointGenerator = entryPointGenerator;
            this.Logger = logger;
            this.AzureClient = azureClient;
        }

        private IAzureClient AzureClient { get;}

        private ILogger? Logger { get; }

        // TODO: EntryPointGenerator might should move out of the Azure Client
        //       project.
        // GitHub Issue: https://github.com/microsoft/iqsharp/issues/610
        private IEntryPointGenerator EntryPointGenerator { get; }

        /// <inheritdoc />
        public override ExecutionResult Run(string input, IChannel channel) =>
            RunAsync(input, channel).Result;

        private static unsafe bool TryParseBitcode(string path, out LLVMModuleRef outModule, out string outMessage)
        {
            LLVMMemoryBufferRef handle;
            sbyte* msg;
            if (LLVM.CreateMemoryBufferWithContentsOfFile(path.AsMarshaledString(), (LLVMOpaqueMemoryBuffer**)&handle, &msg) != 0)
            {
                var span = new ReadOnlySpan<byte>(msg, int.MaxValue);
                var errTxt = span.Slice(0, span.IndexOf((byte)'\0')).AsString();
                LLVM.DisposeMessage(msg);
                throw new InternalCodeGeneratorException(errTxt);
            }

            fixed (LLVMModuleRef* pOutModule = &outModule)
            {
                sbyte* pMessage = null;
                var result = LLVM.ParseBitcodeInContext(
                    LLVM.ContextCreate(),
                    handle,
                    (LLVMOpaqueModule**)pOutModule,
                    &pMessage);

                if (pMessage == null)
                {
                    outMessage = string.Empty;
                }
                else
                {
                    var span = new ReadOnlySpan<byte>(pMessage, int.MaxValue);
                    outMessage = span.Slice(0, span.IndexOf((byte)'\0')).AsString();
                }

                return result == 0;
            }
        }

        /// <summary>
        ///     Simulates an operation given a string with its name and a JSON
        ///     encoding of its arguments.
        /// </summary>
        public async Task<ExecutionResult> RunAsync(string input, IChannel channel)
        {
            var inputParameters = ParseInputParameters(input, firstParameterInferredName: ParameterNameOperationName);

            var name = inputParameters.DecodeParameter<string>(ParameterNameOperationName);
            if (name == null) throw new InvalidOperationException($"No operation name provided.");

            var output = inputParameters.DecodeParameter<string>(ParameterNameOutputPath);

            IEntryPoint? entryPoint;
            try
            {
                var capability = this.AzureClient.TargetCapability;
                var target = this.AzureClient.ActiveTarget?.TargetId;
                // TODO: make sure we end up with a nice error if the operation with that name is not found.
                entryPoint = await EntryPointGenerator.Generate(name, target, capability, generateQir: true);
            }
            catch (TaskCanceledException tce)
            {
                throw tce;
            }
            catch (CompilationErrorsException e)
            {
                var msg = $"The Q# operation {name} could not be compiled as an entry point for job execution.";
                this.Logger?.LogError(e, msg);
                channel?.Stderr(msg);
                channel?.Stderr(e.Message);
                foreach (var message in e.Errors) channel?.Stderr(message);
                return AzureClientError.InvalidEntryPoint.ToExecutionResult();
            }

            if (entryPoint == null)
            {
                return "Internal error: generated entry point was null, but no compilation errors were returned."
                    .ToExecutionResult(ExecuteStatus.Error);
            }

            if (entryPoint.QirStream == null)
            {
                return "Internal error: generated entry point does not contain a QIR bitcode stream, but no compilation errors were returned."
                    .ToExecutionResult(ExecuteStatus.Error);
            }

            var bitcodeFile = Path.ChangeExtension(output ?? Path.GetRandomFileName(), ".bc");
            using (var outStream = File.OpenWrite(bitcodeFile))
            {
                entryPoint.QirStream.CopyTo(outStream);
            }

            // TODO: what if this fails?
            // FIXME: if a user specifies the output file, and runs the command more the once with the same output file
            // then the second time fails due to the file being locked. I think we should just remove the option to specify an outfile. 
            var loaded = TryParseBitcode(bitcodeFile, out var moduleRef, out var parseErr);
            if (string.IsNullOrWhiteSpace(output) || output.Trim().EndsWith(".ll"))
            {
                File.Delete(bitcodeFile);
            }

            var llFile = Path.ChangeExtension(bitcodeFile, ".ll");
            moduleRef.TryPrintToFile(llFile, out var writeErr);
            var llvmIR = File.ReadAllText(llFile);

            if (string.IsNullOrWhiteSpace(output))
            {
                File.Delete(llFile);
            }

            channel?.Stdout(llvmIR);
            return ExecuteStatus.Ok.ToExecutionResult();
        }
    }
}
