namespace ClimbingBot
{
    /// <summary>
    /// Dresses a <see cref="ClimbingWall"/> with holds.
    ///
    /// Different training stages want differently shaped walls, so there is more than one of these.
    /// Put every generator you might want on the wall GameObject and enable exactly one -- the
    /// caller picks the enabled implementation rather than holding a mode enum.
    ///
    /// The wall deliberately knows nothing about these. Where holds go is the generator's decision
    /// during Phase 1 and CV output's decision afterwards.
    /// </summary>
    public interface IWallGenerator
    {
        /// <summary>Clears the wall and re-dresses it. Same seed, same wall.</summary>
        void Generate(int seed);
    }
}
