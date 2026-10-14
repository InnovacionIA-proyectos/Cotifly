using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// #173: registro de las capturas con <c>Idempotency-Key</c> desde que el folio queda confirmado, ANTES de llamar a Nexo. Si la petición
/// falla después de enviar a Nexo (por ejemplo, el guardado del <c>solicitudNexoId</c>), <see cref="IClsAlmacenIdempotencia"/> no guarda
/// el resultado; con este registro el reintento con la misma clave no vuelve a llamar a Nexo: responde con el folio o con
/// <c>ESTADO_NO_PERMITE</c> (<c>detalle.motivo = CAPTURA_EN_CURSO</c>).
/// </summary>
/// <remarks>
/// Vive en la memoria del proceso (singleton), igual que el almacén de idempotencia: se pierde al reiniciar o en un despliegue
/// <c>start-first</c>. La tabla persistente está propuesta en <c>db/scripts/pendientes/06_idempotencia_captura.sql</c> (deuda, ADR-018).
/// </remarks>
public interface IClsRegistroCapturasIdempotentes
{
    /// <summary>La captura registrada con (<paramref name="ambito"/>, <paramref name="clave"/>) y vigente, o <c>null</c>.</summary>
    clsCapturaIdempotente? Buscar(string ambito, string clave);

    /// <summary>Registra o actualiza la captura (al confirmar el folio y, después, con el <c>solicitudNexoId</c>).</summary>
    void Registrar(string ambito, string clave, clsCapturaIdempotente captura);

    /// <summary>
    /// #173: la captura vigente de <paramref name="folio"/> (con cualquier clave), o <c>null</c>. La usa el reintento: si Nexo ya tiene
    /// la solicitud (el guardado posterior falló), se vincula ese id en lugar de reenviar.
    /// </summary>
    clsCapturaIdempotente? BuscarPorFolio(string folio);
}

/// <summary>Una captura con clave: la huella del cuerpo, el folio confirmado y el id de la solicitud de Nexo en cuanto se conoce.</summary>
public sealed record clsCapturaIdempotente(string Huella, Guid CotizacionId, string Folio, string? SolicitudNexoId, DateTimeOffset RegistradaAt);

/// <summary>Implementación en memoria de <see cref="IClsRegistroCapturasIdempotentes"/> (vigencia de 24 h, como la clave).</summary>
public sealed class clsRegistroCapturasIdempotentesEnMemoria(IClsReloj reloj) : IClsRegistroCapturasIdempotentes
{
    /// <summary>Tope de entradas: al llegar, se purgan las vencidas.</summary>
    public const int MaximoEntradas = 10_000;

    private readonly object _candado = new();
    private readonly Dictionary<string, clsCapturaIdempotente> _capturas = new(StringComparer.Ordinal);

    public clsCapturaIdempotente? Buscar(string ambito, string clave)
    {
        lock (_candado)
        {
            return _capturas.TryGetValue(Llave(ambito, clave), out var captura) && Vigente(captura) ? captura : null;
        }
    }

    public void Registrar(string ambito, string clave, clsCapturaIdempotente captura)
    {
        ArgumentNullException.ThrowIfNull(captura);
        lock (_candado)
        {
            if (_capturas.Count >= MaximoEntradas)
            {
                foreach (var vencida in _capturas.Where(c => !Vigente(c.Value)).Select(c => c.Key).ToList())
                {
                    _capturas.Remove(vencida);
                }
            }

            _capturas[Llave(ambito, clave)] = captura;
        }
    }

    public clsCapturaIdempotente? BuscarPorFolio(string folio)
    {
        lock (_candado)
        {
            return _capturas.Values
                .Where(c => Vigente(c) && string.Equals(c.Folio, folio, StringComparison.Ordinal))
                .OrderByDescending(c => c.SolicitudNexoId is not null)
                .FirstOrDefault();
        }
    }

    private bool Vigente(clsCapturaIdempotente captura) => reloj.AhoraUtc - captura.RegistradaAt < IClsAlmacenIdempotencia.Vigencia;

    private static string Llave(string ambito, string clave) => $"{ambito}\n{clave}";
}
