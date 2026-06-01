/* [DV] Created thi file. */

namespace PetaPoco
{
    /// <summary>
    /// Custom parameters to provide to a query call.
    /// Allows you to bypass the 'global' IDatabase settings.
    /// </summary>
    public class QueryOptions
    {
        /// <summary>
        /// Allows you to disable generation of the SELECT statement
        /// when a query does that by itself.
        /// </summary>
        public bool? EnableAutoSelect { get; set; }
    }
}
