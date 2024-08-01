using System;
using System.Collections.Generic;
using System.Text;

namespace LLama
{

    /// <summary>
    /// KoboldCS: Too lazy to pass args through 50 functions until at the end one needs it. I need a good overview over code for Kcs and lsharp
    /// </summary>
    public static class Bridge
    {
        /// <summary>
        /// KoboldCS: Used for context shifting, needed because memory is getting merged into the normal ChatHistory. See next -> 'HandleRunOutOfContext'
        /// </summary>
        public static int MemPos = 0;


        /// <summary>
        /// KoboldCS: This, because else 'HandleRunOutOfContext' would erase half of the context away, which is not what we want. 
        /// </summary>
        public static int MaxTokens = 0;
    }
}
