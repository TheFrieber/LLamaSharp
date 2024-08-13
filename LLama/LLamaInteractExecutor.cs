using LLama.Common;
using LLama.Native;
using LLama.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LLama.Exceptions;
using Microsoft.Extensions.Logging;
using static System.Net.Mime.MediaTypeNames;


namespace LLama
{
    /// <summary>
    /// The LLama executor for interactive mode.
    /// </summary>
    public class InteractiveExecutor : StatefulExecutorBase
    {
        private bool _is_prompt_run = true;
        
        // LLava
        private int _EmbedImagePosition = -1;
        private List<SafeLlavaImageEmbedHandle> _imageEmbedHandles = new List<SafeLlavaImageEmbedHandle>();
        private bool _imageInPrompt = false;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        public InteractiveExecutor(LLamaContext context, ILogger? logger = null)
            : base(context, logger)
        {
        }
        
        public InteractiveExecutor(LLamaContext context, LLavaWeights clipModel, ILogger? logger = null)
            : base(context, clipModel, logger)
        {
        }        

        /// <inheritdoc />
        public override ExecutorBaseState GetStateData()
        {
            InteractiveExecutorState state = new()
            {
                ConsumedSessionCount = _n_session_consumed,
                EmbedInps = _embed_inps.ToArray(),
                IsPromptRun = _is_prompt_run,
                ConsumedTokensCount = _consumedTokensCount,
                Embeds = _embeds.ToArray(),
                LastTokens = _last_n_tokens.ToArray(),
                MatchingSessionTokensCount = _n_matching_session_tokens,
                PastTokensCount = _pastTokensCount,
                SessionFilePath = _pathSession,
                SessionTokens = _session_tokens.ToArray(),
                LastTokensCapacity = _last_n_tokens.Capacity,
                MirostatMu = MirostatMu
            };
            return state;
        }
        /// <inheritdoc />
        public override Task LoadState(ExecutorBaseState data)
        {
            if (data is InteractiveExecutorState state)
            {
                _n_session_consumed = state.ConsumedSessionCount;
                _embed_inps = state.EmbedInps.ToList();
                _is_prompt_run = state.IsPromptRun;
                _consumedTokensCount = state.ConsumedTokensCount;
                _embeds = state.Embeds.ToList();
                _last_n_tokens = new FixedSizeQueue<LLamaToken>(state.LastTokensCapacity, state.LastTokens);
                _n_matching_session_tokens = state.MatchingSessionTokensCount;
                _pastTokensCount = state.PastTokensCount;
                _pathSession = state.SessionFilePath;
                _session_tokens = state.SessionTokens.ToList();
            }
            else
                throw new ArgumentException("Invalid state data type.");

            return Task.CompletedTask;
        }
        /// <inheritdoc />
        public override async Task SaveState(string filename)
        {
            var state = (InteractiveExecutorState)GetStateData();
            using(var fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                await JsonSerializer.SerializeAsync(fs, state);
            }
        }
        /// <inheritdoc />
        public override async Task LoadState(string filename)
        {
            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            {
                var state = await JsonSerializer.DeserializeAsync<InteractiveExecutorState>(fs);
                await LoadState(state);
            }
        }

        /// <summary>
        /// Define whether to continue the loop to generate responses.
        /// </summary>
        /// <returns></returns>
        protected override Task<bool> GetLoopCondition(InferStateArgs args)
        {
            return Task.FromResult(args.RemainedTokens != 0 && !args.WaitForInput || _is_prompt_run);
        }

        /// <inheritdoc />
        protected override Task PreprocessInputs(string? text, InferStateArgs args)
        {
            if (_is_prompt_run)
            {
                // When running the first input (prompt) in interactive mode, we should specially process it.
                if (text == null) throw new ArgumentException("Prompt cannot be null to trigger continuation if a prompt has not been provided previously.");
                if (!this.IsMultiModal)
                {
                    _embed_inps = Context.Tokenize(text, false, true).ToList(); // KoboldCS: We don't use the BOS token bc it makes me a lot work for no difference imo
                }
                else
                {
                    PreprocessLlava(text, args, true);
                }
            }
            else
            {
                // Don't add any tokens if continuation is requested (by providing a null prompt)
                if (text != null)
                {

                    if (!this.IsMultiModal)
                    {
                        var line_inp = Context.Tokenize(text, false, true);
                        _embed_inps.AddRange(line_inp);
                        args.RemainedTokens -= line_inp.Length;
                    }
                    else
                    {
                        PreprocessLlava(text, args, false);
                    }
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        private Task PreprocessLlava(string text, InferStateArgs args, bool addBos = true )
        {
            int usedTokens = 0;
            
            // If the prompt contains the tag <image> extract this.
            _imageInPrompt = text.Contains("<image>");
            if (_imageInPrompt && IsMultiModal )
            {
                foreach (var image in Images)
                {
                    _imageEmbedHandles.Add(SafeLlavaImageEmbedHandle.CreateFromMemory(ClipModel.NativeHandle, Context, image));
                }

                int imageIndex = text.IndexOf("<image>");
                // Tokenize segment 1 (before <image> tag)
                string preImagePrompt = text.Substring(0, imageIndex);
                var segment1 = Context.Tokenize(preImagePrompt, addBos, true);
                // Remember the position to add the image embeddings
                _EmbedImagePosition = segment1.Length;
                string postImagePrompt = text.Substring(imageIndex + 7);
                var segment2 = Context.Tokenize(postImagePrompt, false, true);
                _embed_inps.AddRange(segment1);
                _embed_inps.AddRange(segment2);
                usedTokens += (segment1.Length + segment2.Length);
            }
            else
            {
                if (addBos)
                {
                    _embed_inps = Context.Tokenize(text, true, true).ToList();
                }
                else
                {
                    var line_inp = Context.Tokenize(text, false, true);
                    _embed_inps.AddRange(line_inp);
                    args.RemainedTokens -= line_inp.Length;                    
                }
            }
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Return whether to break the generation.
        /// </summary>
        /// <param name="inferenceParams"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        protected override async Task<(bool, IReadOnlyList<string>)> PostProcess(IInferenceParams inferenceParams, InferStateArgs args)
        {
            if (_embed_inps.Count <= _consumedTokensCount)
            {
                if (_last_n_tokens.TokensEndsWithAnyString(args.Antiprompts, Context.NativeHandle.ModelHandle, Context.Encoding))
                    args.WaitForInput = true;

                if (_pastTokensCount > 0 && args.WaitForInput)
                    return (true, Array.Empty<string>());
            }

            if (_embeds.Count > 0 && _embeds.Last() == Context.Tokens.EOS)
            {
                return (true, new[] { " [end of text]\n" });
            }

            if (args.RemainedTokens <= 0 && inferenceParams.MaxTokens != -1)
            {
                args.RemainedTokens = inferenceParams.MaxTokens;
                args.WaitForInput = true;
            }

            return (false, Array.Empty<string>());
        }

        /// <inheritdoc />
        protected override async Task InferInternal(IInferenceParams inferenceParams, InferStateArgs args)
        {
            try
            {
                var batch = new LLamaBatch();

                if (_embeds.Count > 0)
                {
                    checkAgain:
                    if (_pastTokensCount + _embeds.Count > Context.ContextSize)
                    {
                        // number of tokens to keep when resetting context
                        // Ported from https://github.com/ggerganov/llama.cpp/blob/60325fa56f61c228464c9f065db3aa6a61f2156e/examples/main/main.cpp#L334
                        var tokensToKeep = inferenceParams.TokensKeep;
                        if (tokensToKeep < 0 || tokensToKeep > _embed_inps.Count)
                        {
                            tokensToKeep = _embed_inps.Count;
                        }
                        else
                        {
                            tokensToKeep += Convert.ToInt32(Context.ShouldAddBosToken()); // always keep the BOS token
                        }

                        HandleRunOutOfContext(tokensToKeep);
                        goto checkAgain;
                    }
                    TryReuseMatchingPrefix();

                    // Changes to support Multi-Modal LLMs.
                    //
                    (DecodeResult, int, int) header, end, result;
                    if (IsMultiModal && _EmbedImagePosition > 0)
                    {
                        // Tokens previous to the images
                        header = await Context.DecodeAsync(_embeds.GetRange(0, _EmbedImagePosition), LLamaSeqId.Zero, batch, _pastTokensCount);
                        _pastTokensCount = header.Item3;

                        if (header.Item1 != DecodeResult.Ok)
                        {
                            Console.WriteLine("[Decode Error}");
                        }
                            //throw new LLamaDecodeError(header.Item1);

                        // Images
                        foreach (var image in _imageEmbedHandles)
                            ClipModel.EvalImageEmbed(Context, image, ref _pastTokensCount);

                        // Post-image Tokens
                        end = await Context.DecodeAsync(_embeds.GetRange(_EmbedImagePosition, _embeds.Count - _EmbedImagePosition), LLamaSeqId.Zero, batch, _pastTokensCount);
                        _pastTokensCount = end.Item3;

                        _EmbedImagePosition = -1;
                        _imageEmbedHandles.Clear();
                        Images.Clear();
                    }
                    else
                    {
                        if (_is_prompt_run)
                        {
                            _embeds.RemoveRange(0, _pastTokensCount);
                            _is_prompt_run = false;
                        }
                        result = await Context.DecodeAsync(_embeds, LLamaSeqId.Zero, batch, _pastTokensCount);
                        _pastTokensCount = result.Item3;
                        if (result.Item1 != DecodeResult.Ok)
                        {
                            Console.WriteLine("[Decode Error}");
                        }

                        //throw new LLamaDecodeError(result.Item1);
                    }

                    if (_embeds.Count > 0 && !string.IsNullOrEmpty(_pathSession))
                    {
                        _session_tokens.AddRange(_embeds);
                        _n_session_consumed = _session_tokens.Count;
                    }
                }
                _embeds.Clear();

                if (_embed_inps.Count <= _consumedTokensCount && !args.WaitForInput)
                {
                    var repeat_last_n = inferenceParams.RepeatLastTokensCount < 0 ? (int)Context.ContextSize : inferenceParams.RepeatLastTokensCount;

                    // optionally save the session on first sample (for faster prompt loading next time)
                    if (!string.IsNullOrEmpty(_pathSession) && args.NeedToSaveSession)
                    {
                        args.NeedToSaveSession = false;
                        SaveSessionFile(_pathSession);
                    }
                    LLamaToken id;
                    if (inferenceParams.SamplingPipeline is not null)
                    {
                        id = inferenceParams.SamplingPipeline.Sample(Context.NativeHandle, Context.NativeHandle.GetLogitsIth(batch.TokenCount - 1), _last_n_tokens.ToArray());
                        inferenceParams.SamplingPipeline.Accept(Context.NativeHandle, id);
                    }
                    else
                    {
                        var tokenDataArray = Context.ApplyPenalty(batch.TokenCount - 1, _last_n_tokens, inferenceParams.LogitBias, repeat_last_n,
                            inferenceParams.RepeatPenalty, inferenceParams.FrequencyPenalty, inferenceParams.PresencePenalty, inferenceParams.PenalizeNL);

                        var mu = MirostatMu;
                        id = Context.Sample(
                            tokenDataArray, ref mu, inferenceParams.Temperature, inferenceParams.Mirostat, inferenceParams.MirostatTau,
                            inferenceParams.MirostatEta, inferenceParams.TopK, inferenceParams.TopP, inferenceParams.TfsZ, inferenceParams.TypicalP, inferenceParams.Grammar,
                            inferenceParams.MinP
                        );

                        MirostatMu = mu;
                    }
                    _last_n_tokens.Enqueue(id);

                    if (id == Context.NativeHandle.ModelHandle.Tokens.EOS)
                    {
                        id = Context.NativeHandle.ModelHandle.Tokens.Newline!.Value;
                        if (args.Antiprompts is not null && args.Antiprompts.Count > 0)
                        {
                            var first_antiprompt = Context.Tokenize(args.Antiprompts[0], false);
                            _embed_inps.AddRange(first_antiprompt);
                        }
                    }
                    _embeds.Add(id);

                    args.RemainedTokens--;
                    args.ReturnValue = true;
                }
                else
                {
                    while (_embed_inps.Count > _consumedTokensCount)
                    {
                        _embeds.Add(_embed_inps[_consumedTokensCount]);
                        _last_n_tokens.Enqueue(_embed_inps[_consumedTokensCount]);
                        _consumedTokensCount++;
                        if (_embeds.Count >= Context.BatchSize)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return;
        }










        /// <summary>
        /// KoboldCS: Help the Synchronizer back to Synchronize by reprocessing last response if the model splitted things into multiple tokens.
        /// </summary>
        /// <param name="lastResponse"></param>
        /// <returns></returns>
        public async Task ReDecode(List<LLamaToken> lastResponse, int beginningPos)
        {
            try
            {
                checkAgain:
                if ((lastResponse.Count + Context.NativeHandle.KvCacheCountCells()) >= Context.ContextSize)
                {
                    HandleRunOutOfContext(0);
                    goto checkAgain;
                }

                //Remove the faulty response.
                Context.NativeHandle.KvCacheRemove(LLamaSeqId.Zero, beginningPos, Context.NativeHandle.KvCacheCountCells());
                Context.NativeHandle.KvCacheUpdate();

                var batch = new LLamaBatch();

                (DecodeResult, int, int) result;
                result = await Context.DecodeAsync(lastResponse, LLamaSeqId.Zero, batch, Context.NativeHandle.KvCacheCountCells());
                _pastTokensCount = result.Item3;
                if (result.Item1 != DecodeResult.Ok)
                {
                    Console.WriteLine("[ReDecode Error]");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return;
        }

        /// <summary>
        /// KoboldCS: Instead of clearing the whole cache, setting it up again then messing the Synchronizer up because we can't measure before generation.. Just overwrite it in SetContext
        /// </summary>
        /// <param name="lastResponse"></param>
        /// <param name="mempos">We also want to update the Memory. This defines the Tokencount of the memory. After that we seperate excess.</param>
        /// <returns></returns>
        public async Task UpdateWithDecode(List<LLamaToken> lastResponse, int mempos)
        {
            if (lastResponse.Count == 0) return;

            try
            {
                var batch = new LLamaBatch();

                // Extract memory tokens from lastResponse
                List<LLamaToken> memoryTokens = lastResponse.GetRange(0, mempos);
                lastResponse.RemoveRange(0, mempos);

                int totalSize = memoryTokens.Count + lastResponse.Count;

                // remove from the lastResponse until it fits the context
                if (totalSize >= Context.ContextSize)
                {
                    // Calculate the excess amount that needs to be removed
                    int excess = totalSize - (int)Context.ContextSize;

                    // Remove that many elements from the beginning of lastResponse
                    if (excess > 0 && excess <= lastResponse.Count)
                    {
                        lastResponse.RemoveRange(0, excess + 1); // +1 to be save it fits within the context size
                    }
                }

                // And now combine memoryTokens and lastResponse back together for Decoding.
                memoryTokens.AddRange(lastResponse);

                Context.NativeHandle.KvCacheClear();
                (DecodeResult, int, int) result;
                result = await Context.DecodeAsync(memoryTokens, LLamaSeqId.Zero, batch, 0);
                Console.WriteLine("Updated: " + Context.NativeHandle.KvCacheCountCells() + "\n" + "Updated H: " + memoryTokens.Count + "\n" + "TotalSize: " + totalSize);
                _pastTokensCount = result.Item3;
                if (result.Item1 != DecodeResult.Ok)
                {
                    Console.WriteLine("[UpdateWithDecode Error]");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return;
        }

        /// <summary>
        /// KoboldCS: There is a bug with the last char not getting decoded and stays in the _embeds container for the next gen. This is an issue we do need before trimming in the ctx
        /// </summary>
        /// <returns></returns>
        public async Task Cleanup()
        {

            try
            {
                if(_embeds.Count == 1)
                {
                    var batch = new LLamaBatch();


                    (DecodeResult, int, int) result;
                    result = await Context.DecodeAsync(_embeds, LLamaSeqId.Zero, batch, _pastTokensCount);
                    _pastTokensCount = result.Item3;
                    if (result.Item1 != DecodeResult.Ok)
                    {
                        Console.WriteLine("[Cleanup Error]");
                    }
                    _embeds.Clear();
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return;
        }

        /// <summary>
        /// KoboldCS: When doing stuff with context, we do want that llamaSharp knows it. This is an access port simple said.
        /// </summary>
        /// <returns></returns>
        public int PastTokensCount
        {
            get { return _pastTokensCount; }
            set { _pastTokensCount = value; }
        }


        /// <summary>
        /// The descriptor of the state of the interactive executor.
        /// </summary>
        public class InteractiveExecutorState
            : ExecutorBaseState
        {
            /// <summary>
            /// Whether the executor is running for the first time (running the prompt).
            /// </summary>
            [JsonPropertyName("is_prompt_run")]
            public bool IsPromptRun { get; set; }
        }
    }
}
