using Pathoschild.Stardew.LookupAnything.Framework.Constants;

namespace Pathoschild.Stardew.LookupAnything.Framework.Models
{
    /// <summary>An item slot for a bundle.</summary>
    internal class BundleIngredientModel
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The ingredient's index in the bundle.</summary>
        public int Index { get; }

        /// <summary>The required item's qualified or unqualified item ID (or -1 for a monetary bundle).</summary>
        public string ItemId { get; }

        /// <summary>The number of items required.</summary>
        public int Stack { get; }

        /// <summary>The required item quality.</summary>
        public ItemQuality Quality { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="index">The ingredient's index in the bundle.</param>
        /// <param name="itemId">The required item's qualified or unqualified item ID (or -1 for a monetary bundle).</param>
        /// <param name="stack">The number of items required.</param>
        /// <param name="quality">The required item quality.</param>
        public BundleIngredientModel(int index, string itemId, int stack, ItemQuality quality)
        {
            this.Index = index;
            this.ItemId = itemId;
            this.Stack = stack;
            this.Quality = quality;
        }
    }
}
