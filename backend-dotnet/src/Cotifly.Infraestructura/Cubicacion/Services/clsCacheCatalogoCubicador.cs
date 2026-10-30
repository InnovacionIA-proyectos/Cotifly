namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Caché en memoria (singleton, una por catálogo: <typeparamref name="TFila"/>) de los catálogos del cubicador (tipos de mercancía,
/// carrocerías y vehículos; HU-021.5, #133, y HU-023.1, #156), como la de los catálogos maestros de Nexo: solo guarda respuestas
/// exitosas y vence a los <c>Cubicador:MinutosCacheCatalogos</c>. Sin copia de respaldo: si el cubicador no responde, el endpoint del
/// catálogo responde 503.
/// </summary>
public sealed class clsCacheCatalogoCubicador<TFila>(TimeProvider reloj)
{
    private readonly Lock _candado = new();
    private (IReadOnlyList<TFila> Filas, DateTimeOffset Vence)? _copia;

    /// <summary>El catálogo guardado si no ha vencido; si no, <c>null</c>.</summary>
    public IReadOnlyList<TFila>? Obtener()
    {
        lock (_candado)
        {
            return _copia is { } copia && reloj.GetUtcNow() < copia.Vence ? copia.Filas : null;
        }
    }

    /// <summary>Guarda el catálogo por <paramref name="duracion"/>.</summary>
    public void Guardar(IReadOnlyList<TFila> filas, TimeSpan duracion)
    {
        ArgumentNullException.ThrowIfNull(filas);
        lock (_candado)
        {
            _copia = (filas, reloj.GetUtcNow() + duracion);
        }
    }
}
