namespace Pathoschild.Stardew.LookupAnything.Framework.Data
{
    /// <summary>Metadata for a machine's recipes.</summary>
    internal class MachineRecipesData
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The machine's unqualified item ID.</summary>
        public string MachineID { get; set; }

        /// <summary>The machine recipes.</summary>
        public MachineRecipeData[] Recipes { get; set; }
    }
}
