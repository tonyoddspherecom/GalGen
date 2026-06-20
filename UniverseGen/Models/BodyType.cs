namespace UniverseGen.Models;

/// <summary>Primary classification of a planetary body.</summary>
public enum BodyType
{
    Terrestrial,    // rocky, Earth-scale, tectonic activity possible
    GasGiant,       // Jupiter/Saturn-like: hydrogen-helium envelope, > 50 M♁
    IceGiant,       // Neptune/Uranus-like: ice + gas, 10–50 M♁
    OceanWorld,     // surface dominated by liquid water
    Desert,         // hot, dry, minimal water
    Lava,           // volcanically extreme; orbital heat or tidal forcing
    Frozen,         // ice-dominated, beyond snow line, < 10 M♁
    Airless,        // bare rock, no meaningful atmosphere
    AsteroidBelt,   // debris ring — not a solid body
}

/// <summary>Atmosphere composition class.</summary>
public enum AtmosphereType
{
    None,           // no measurable atmosphere (vacuum/trace)
    Thin,           // < 0.1 atm surface pressure
    Thick,          // > 3 atm (gas giants, Venus-like dense)
    Toxic,          // poisonous (CO₂, SO₂, ammonia dominates)
    Breathable,     // O₂/N₂ mix, surface-pressure liveable
}

/// <summary>Dominant surface character.</summary>
public enum SurfaceType
{
    Gaseous,        // no solid surface (gas/ice giants)
    Volcanic,       // active lava flows, lava lakes
    Cratered,       // ancient, heavily impacted crust
    Mountainous,    // active tectonics, varied high relief
    Flat,           // plains, low topography
    Oceanic,        // liquid-water dominated surface
    Icy,            // ice sheets, glaciers, frozen plains
}
