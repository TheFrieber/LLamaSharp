﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama.Abstractions;
using LLama.Exceptions;
using LLama.Extensions;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace LLama
{
    /// <summary>
    /// A set of model weights, loaded into memory.
    /// </summary>
    public sealed class LLamaWeights
        : IDisposable
    {
        /// <summary>
        /// The native handle, which is used in the native APIs
        /// </summary>
        /// <remarks>Be careful how you use this!</remarks>
        public SafeLlamaModelHandle NativeHandle { get; }

        /// <summary>
        /// Total number of tokens in vocabulary of this model
        /// </summary>
        public int VocabCount => NativeHandle.VocabCount;

        /// <summary>
        /// Total number of tokens in the context
        /// </summary>
        public int ContextSize => NativeHandle.ContextSize;

        /// <summary>
        /// Get the size of this model in bytes
        /// </summary>
        public ulong SizeInBytes => NativeHandle.SizeInBytes;

        /// <summary>
        /// Get the number of parameters in this model
        /// </summary>
        public ulong ParameterCount => NativeHandle.ParameterCount;

        /// <summary>
        /// Dimension of embedding vectors
        /// </summary>
        public int EmbeddingSize => NativeHandle.EmbeddingSize;

        /// <summary>
        /// Get the special tokens of this model
        /// </summary>
        public SafeLlamaModelHandle.ModelTokens Tokens => NativeHandle.Tokens;

        /// <summary>
        /// All metadata keys in this model
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }

        private LLamaWeights(SafeLlamaModelHandle weights)
        {
            NativeHandle = weights;
            Metadata = weights.ReadMetadata();
        }

        /// <summary>
        /// Load weights into memory
        /// </summary>
        /// <param name="params"></param>
        /// <returns></returns>
        public static LLamaWeights LoadFromFile(IModelParams @params)
        {
            using var pin = @params.ToLlamaModelParams(out var lparams);
            var weights = SafeLlamaModelHandle.LoadFromFile(@params.ModelPath, lparams);

            foreach (var adapter in @params.LoraAdapters)
            {
                if (string.IsNullOrEmpty(adapter.Path))
                    continue;
                if (adapter.Scale <= 0)
                    continue;

                weights.ApplyLoraFromFile(adapter.Path, adapter.Scale, @params.LoraBase);
            }

            return new LLamaWeights(weights);
        }

        /// <summary>
        /// Load weights into memory
        /// </summary>
        /// <param name="params">Parameters to use to load the model</param>
        /// <param name="token">A cancellation token that can interrupt model loading</param>
        /// <returns></returns>
        /// <exception cref="LoadWeightsFailedException">Thrown if weights failed to load for any reason. e.g. Invalid file format or loading cancelled.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the cancellation token is cancelled.</exception>
        public static async Task<LLamaWeights> LoadFromFileAsync(IModelParams @params, CancellationToken token = default)
        {
            // don't touch the @params object inside the task, it might be changed
            // externally! Save a copy of everything that we need later.
            var modelPath = @params.ModelPath;
            var loraBase = @params.LoraBase;
            var loraAdapters = @params.LoraAdapters.ToArray();

            using (@params.ToLlamaModelParams(out var lparams))
            {
#if !NETSTANDARD2_0
                // Overwrite the progress callback with one which polls the cancellation token
                //if (token.CanBeCanceled)
                {
                    var internalCallback = lparams.progress_callback;
                    lparams.progress_callback = (progress, ctx) =>
                    {
                        // If the user set a call in the model params, first call that and see if we should cancel
                        if (internalCallback != null && !internalCallback(progress, ctx))
                            return false;

                        // Check the cancellation token
                        if (token.IsCancellationRequested)
                            return false;

                        return true;
                    };
                }
#endif

                var model = await Task.Run(() =>
                {
                    try
                    {
                        var weights = SafeLlamaModelHandle.LoadFromFile(modelPath, lparams);
                        foreach (var adapter in loraAdapters)
                        {
                            // Interrupt applying LoRAs if the token is cancelled
                            if (token.IsCancellationRequested)
                            {
                                weights.Dispose();
                                token.ThrowIfCancellationRequested();
                            }

                            // Don't apply invalid adapters
                            if (string.IsNullOrEmpty(adapter.Path))
                                continue;
                            if (adapter.Scale <= 0)
                                continue;

                            weights.ApplyLoraFromFile(adapter.Path, adapter.Scale, loraBase);
                        }

                        return new LLamaWeights(weights);
                    }
                    catch (LoadWeightsFailedException)
                    {
                        // Convert a LoadWeightsFailedException into a cancellation exception if possible.
                        token.ThrowIfCancellationRequested();

                        // Ok the weights failed to load for some reason other than cancellation.
                        throw;
                    }
                }, token);

                return model;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NativeHandle.Dispose();
        }

        /// <summary>
        /// Create a llama_context using this model
        /// </summary>
        /// <param name="params"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public LLamaContext CreateContext(IContextParams @params, ILogger? logger = null)
        {
            return new LLamaContext(this, @params, logger);
        }

        /// <summary>
        /// Convert a string of text into tokens
        /// </summary>
        /// <param name="text"></param>
        /// <param name="add_bos"></param>
        /// <param name="encoding"></param>
        /// <param name="special">Allow tokenizing special and/or control tokens which otherwise are not exposed and treated as plaintext.</param>
        /// <returns></returns>
        public LLamaToken[] Tokenize(string text, bool add_bos, bool special, Encoding encoding)
        {
            return NativeHandle.Tokenize(text, add_bos, special, encoding);
        }
    }
}
