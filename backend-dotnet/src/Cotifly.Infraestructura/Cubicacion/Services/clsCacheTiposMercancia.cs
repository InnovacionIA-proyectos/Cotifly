using Cotifly.Aplicacion.Cubicacion.DTOs;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Caché en memoria (singleton) del catálogo de tipos de mercancía del cubicador (HU-021.5, #133), como la de los catálogos maestros de
/// Nexo: solo guarda respuestas exitosas y vence a los <c>Cubicador:MinutosCacheCatalogos</c>. Sin copia de respaldo: si el cubicador no
/// responde, la captura conserva el nombre que mandó el cliente y <c>GET /web/tipos-mercancia</c> responde 503.
/// </summary>
public sealed class clsCacheTiposMercancia(TimeProvider reloj)
{
    private readonly Lock _candado = new();
    private (IReadOnlyList<clsTipoMercanciaCubicador> Tipos, DateTimeOffset Vence)? _copia;

    /// <summary>El catálogo guardado si no ha vencido; si no, <c>null</c>.</summary>
    public IReadOnlyList<clsTipoMercanciaCubicador>? Obtener()
    {
        lock (_candado)
        {
            return _copia is { } copia && reloj.GetUtcNow() < copia.Vence ? copia.Tipos : null;
        }
    }

    /// <summary>Guarda el catálogo por <paramref name="duracion"/>.</summary>
    public void Guardar(IReadOnlyList<clsTipoMercanciaCubicador> tipos, TimeSpan duracion)
    {
        ArgumentNullException.ThrowIfNull(tipos);
        lock (_candado)
        {
            _copia = (tipos, reloj.GetUtcNow() + duracion);
        }
    }
}
