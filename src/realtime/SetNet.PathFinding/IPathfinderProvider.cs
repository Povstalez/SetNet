namespace SetNet.PathFinding
{
    /// <summary>
    /// Геодата, яка сама знає, чим по ній шукати шлях.
    ///
    /// <para>
    /// Потрібно, щоб <see cref="Pathfinding.For"/> не мусив знати про кожну
    /// реалізацію <c>IGeoData</c> поіменно. Формат, що живе в чужому проєкті,
    /// інакше або змушував би правити цю бібліотеку під себе, або падав тут
    /// винятком — а падав би не при завантаженні геодати, а пізніше, у першого
    /// ж, хто спробує кудись піти.
    /// </para>
    /// </summary>
    public interface IPathfinderProvider
    {
        /// <summary>Створює пошуковик для себе.</summary>
        IPathfinder CreatePathfinder();
    }
}
