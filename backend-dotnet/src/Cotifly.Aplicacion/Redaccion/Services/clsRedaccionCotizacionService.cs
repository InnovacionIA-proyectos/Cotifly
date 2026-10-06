using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Redaccion.Services;

/// <summary>
/// Redacción del asunto y la introducción del PDF con la IA y el texto estándar de respaldo (HU-013.5, ADR-018 §2a). Nunca falla por
/// la IA: siempre devuelve textos válidos.
/// </summary>
public interface IClsRedaccionCotizacionService
{
    /// <summary>Versión (hash) del prompt del redactor, para la auditoría.</summary>
    string VersionPrompt { get; }

    /// <summary>
    /// Pide los textos a <see cref="IClsRedactorCotizacion"/> con el tiempo máximo de <see cref="clsOpcionesRedaccion"/>. Si tarda, falla
    /// o la salida no pasa <see cref="clsReglasTextosCotizacion"/>, devuelve <paramref name="estandar"/> (exactamente los que imprime el
    /// PDF de la variante) con <c>fuente = estandar</c>. Solo la cancelación del llamador se propaga. No audita: lo hace el caso de
    /// uso después de guardar, con el resultado de la escritura.
    /// </summary>
    Task<clsResultadoRedaccion> RedactarAsync(clsContextoRedaccion contexto, clsTextosBase estandar, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsRedaccionCotizacionService"/>. El tiempo máximo corre sobre el <see cref="TimeProvider"/> inyectado
/// (las pruebas lo adelantan sin esperar) y corta la espera aunque el proveedor no atienda la cancelación
/// (<see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/>).
/// </summary>
public sealed class clsRedaccionCotizacionService(
    IClsRedactorCotizacion redactor,
    IOptions<clsOpcionesRedaccion> opciones,
    TimeProvider tiempo,
    ILogger<clsRedaccionCotizacionService> logger) : IClsRedaccionCotizacionService
{
    /// <summary>Herramienta de la auditoría de la redacción (<c>EventosHerramienta.herramienta</c>; la registra el caso de uso).</summary>
    public const string HerramientaAuditoria = "redactar_textos_costeo";

    // Motivos del respaldo que no son de validación (los de validación son los de clsReglasTextosCotizacion).
    public const string MotivoTiempoAgotado = "tiempo_agotado";
    public const string MotivoIaNoDisponible = "ia_no_disponible";

    public string VersionPrompt => redactor.Identidad.VersionPrompt;

    public async Task<clsResultadoRedaccion> RedactarAsync(clsContextoRedaccion contexto, clsTextosBase estandar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(estandar);
        var identidad = redactor.Identidad;
        var limite = TimeSpan.FromSeconds(opciones.Value.TiempoRedaccionSegundos);
        var cronometro = Stopwatch.StartNew();
        string? modelo = null;
        string? motivo;
        clsValidacionTextosCotizacion? validacion = null;

        using (var plazo = new CancellationTokenSource(limite, tiempo))
        using (var corte = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plazo.Token))
        {
            try
            {
                var respuesta = await redactor.RedactarAsync(contexto, corte.Token).WaitAsync(limite, tiempo, cancellationToken);
                modelo = respuesta.Modelo;
                validacion = clsReglasTextosCotizacion.InterpretarSalidaIa(respuesta.Texto, Permitidos(contexto));
                motivo = validacion.Motivo;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                motivo = MotivoTiempoAgotado;
            }
            catch (Exception ex)
            {
                // El proveedor falló (caído, 429, 5xx, red): la causa queda en el log, sin el contexto ni la clave.
                logger.LogWarning("El redactor de la IA falló ({Tipo}); se usan los textos estándar.", ex.GetType().Name);
                motivo = MotivoIaNoDisponible;
            }
        }

        var duracion = (int)Math.Min(int.MaxValue, cronometro.ElapsedMilliseconds);
        if (motivo is not null)
        {
            logger.LogInformation("La redacción usa los textos estándar: {Motivo}.", motivo);
            return new clsResultadoRedaccion(estandar.Asunto, estandar.Introduccion, clsTextosCotizacion.FuenteEstandar,
                identidad.Proveedor, modelo ?? identidad.Modelo, identidad.VersionPrompt, duracion, Respaldo: true, motivo);
        }

        return new clsResultadoRedaccion(validacion!.Asunto!, validacion.Introduccion!, clsTextosCotizacion.FuenteIa,
            identidad.Proveedor, modelo ?? identidad.Modelo, identidad.VersionPrompt, duracion, Respaldo: false, null);
    }

    /// <summary>Textos del contexto que el modelo puede repetir literalmente (no cuentan como cifras en la validación).</summary>
    public static IEnumerable<string?> Permitidos(clsContextoRedaccion contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        return new[] { contexto.Cliente, contexto.LineaNegocio, contexto.TipoVehiculo }
            .Concat(contexto.Rutas.SelectMany(r => new[] { r.Origen, r.Destino }))
            .Concat(contexto.Conexos);
    }

    /// <summary>SHA-256 (hexadecimal en minúsculas) del asunto y la introducción: identifica el texto en la auditoría sin guardarlo.</summary>
    public static string Huella(string asunto, string introduccion)
    {
        ArgumentNullException.ThrowIfNull(asunto);
        ArgumentNullException.ThrowIfNull(introduccion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(asunto + "\n\n" + introduccion)));
    }
}
