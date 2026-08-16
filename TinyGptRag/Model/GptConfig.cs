namespace TinyGptRag.Model
{
    /// <summary>Hyperparameters for the from-scratch transformer.</summary>
    public class GptConfig
    {
        public int VocabSize { get; set; } = 4000;
        public int DModel { get; set; } = 64;      // embedding / hidden dimension
        public int NHead { get; set; } = 4;        // attention heads (DModel must be divisible by this)
        public int NLayer { get; set; } = 4;        // transformer blocks
        public int DFeedForward { get; set; } = 256; // MLP inner dimension
        public int BlockSize { get; set; } = 128;    // max context length (tokens)

        public int HeadDim => DModel / NHead;
    }
}
