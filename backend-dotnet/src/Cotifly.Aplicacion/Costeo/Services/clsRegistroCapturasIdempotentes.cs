using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// #173: registro de las capturas con <c>Idempotency-Key</c> desde que el folio queda confirmado, ANTES de llamar a Nexo. Si la petición
/// falla después de enviar a Nexo (por ejemplo, el guardado del <c>solicitudNexoId</c>), <see cref="IClsAlmacenIdempotencia"/> no guarda
/// el resultado; con este registro el reintento con la misma clave no vuelve a llamar a Nexo: responde con el folio o con
/// <c>ESTADO_NO_PERMITE</c> (<c>detalle.motivo = CAPTURA_EN_CURSO</c>).
/// </summary>
/// <remarks>
/// #191: la implementación de la API es <c>clsRegistroCapturasIdempotentesSql</c> (Infraestructura), sobre la tabla
/// <c>dbo_cotifly.CapturasIdempotentes</c> de <c>db/scripts/06_idempotencia_captura.sql</c>, la misma fila que reserva la clave en
/// <c>clsAlmacenIdempotenciaSql</c>: sobrevive a un reinicio y a un despliegue <c>start-first</c> (dos réplicas a la vez).
/// <see cref="RegistrarAsync"/> participa en la transacción abierta del <c>DbContext</c> del ámbito: la captura lo llama dentro de la
/// transacción que confirma el folio, así el folio y su clave quedan juntos o no queda ninguno. <see cref="clsRegistroCapturasIdempotentesEnMemoria"/>
/// queda para las pruebas de Aplicación.
/// </remarks>
public interface IClsRegistroCapturasIdempotentes
{
    /// <summary>La captura registrada con (<paramref name="ambito"/>, <paramref name="clave"/>), vigente y con folio, o <c>null</c>.</summary>
    Task<clsCapturaIdempotente?> BuscarAsync(string ambito, string clave, CancellationToken cancellationToken = default);

    /// <summary>Registra o actualiza la captura (al confirmar el folio y, después, con el <c>solicitudNexoId</c>).</summary>
    Task RegistrarAsync(string ambito, string clave, clsCapturaIdempotente captura, CancellationToken cancellationToken = default);

    /// <summary>
    /// #173: la captura vigente de <paramref name="folio"/> (con cualquier clave), o <c>null</c>. La usa el reintento: si Nexo ya tiene
    /// la solicitud (el guardado posterior falló), se vincula ese id en lugar de reenviar.
    /// </summary>
    Task<clsCapturaIdempotente?> BuscarPorFolioAsync(string folio, CancellationToken cancellationToken = default);
}

/// <summary>
/// Una captura con clave: la huella del cuerpo, el folio confirmado y el id de la solicitud de Nexo en cuanto se conoce, con el
/// <c>scorePrioridad</c> que Nexo devolvió con ese id (HU-018.1, #155). El reintento que vincula por el registro (#173) lo guarda igual
/// que el primer envío; <c>null</c> si Nexo no lo mandó o el registro es anterior a este campo (el vínculo sigue, sin score).
/// </summary>
public sealed record clsCapturaIdempotente(
    string Huella, Guid CotizacionId, string Folio, string? SolicitudNexoId, DateTimeOffset RegistradaAt, decimal? ScorePrioridad = null);

/// <summary>
/// Implementación en memoria de <see cref="IClsRegistroCapturasIdempotentes"/> (vigencia de 24 h, como la clave). Solo para pruebas: se
/// pierde al reiniciar y no se comparte entre réplicas (la API usa la tabla del 06, #191).
/// </summary>
public sealed class clsRegistroCapturasIdempotentesEnMemoria(IClsReloj reloj) : IClsRegistroCapturasIdempotentes
{
    /// <summary>Tope de entradas: al llegar, se purgan las vencidas.</summary>
    public const int MaximoEntradas = 10_000;

    private readonly object _candado = new();
    private readonly Dictionary<string, clsCapturaIdempotente> _capturas = new(StringComparer.Ordinal);

    public Task<clsCapturaIdempotente?> BuscarAsync(string ambito, string clave, CancellationToken cancellationToken = default)
    {
        lock (_candado)
        {
            return Task.FromResult(_capturas.TryGetValue(Llave(ambito, clave), out var captura) && Vigente(captura) ? captura : null);
        }
    }

    public Task RegistrarAsync(string ambito, string clave, clsCapturaIdempotente captura, CancellationToken cancellationToken = default)
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

        return Task.CompletedTask;
    }

    public Task<clsCapturaIdempotente?> BuscarPorFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        lock (_candado)
        {
            return Task.FromResult(_capturas.Values
                .Where(c => Vigente(c) && string.Equals(c.Folio, folio, StringComparison.Ordinal))
                .OrderByDescending(c => c.SolicitudNexoId is not null)
                .FirstOrDefault());
        }
    }

    private bool Vigente(clsCapturaIdempotente captura) => reloj.AhoraUtc - captura.RegistradaAt < IClsAlmacenIdempotencia.Vigencia;

    private static string Llave(string ambito, string clave) => $"{ambito}\n{clave}";
}
