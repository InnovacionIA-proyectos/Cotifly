using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Candado transaccional de SQL Server (<c>sp_getapplock</c>, sección <c>CandadoSql</c>) que serializa el folio (ADR-011).
/// La sección crítica dura milisegundos (consecutivo + inserción); estos valores solo importan con la base degradada.
/// </summary>
public sealed class clsOpcionesCandadoSql
{
    public const string Seccion = "CandadoSql";

    /// <summary>
    /// Espera máxima por el candado en cada intento (<c>@LockTimeout</c> de <c>sp_getapplock</c>, <c>CandadoSql__EsperaMilisegundos</c>).
    /// Por defecto 5 s.
    /// </summary>
    [Range(100, 60_000)]
    public int EsperaMilisegundos { get; set; } = 5_000;

    /// <summary>
    /// Margen sobre <see cref="EsperaMilisegundos"/> para la respuesta del servidor; pasado ese total el cliente cancela la solicitud.
    /// Por defecto 15 s.
    /// </summary>
    [Range(1_000, 120_000)]
    public int MargenRespuestaMilisegundos { get; set; } = 15_000;

    /// <summary>
    /// Tiempo que se espera el acuse del servidor tras cancelar la solicitud (al vencer el margen); pasado ese tiempo la conexión se
    /// cierra a la fuerza para que el servidor deshaga la transacción y suelte el candado. Por defecto 5 s.
    /// </summary>
    [Range(500, 60_000)]
    public int GraciaCancelacionMilisegundos { get; set; } = 5_000;

    /// <summary>
    /// <c>CommandTimeout</c> de respaldo del <c>sp_getapplock</c> (s). Alto a propósito: SqlClient lo mide con el reloj de pared y un
    /// salto del reloj lo vencería al instante; el límite normal es el monotónico de arriba. Por defecto 120.
    /// </summary>
    [Range(5, 3_600)]
    public int ComandoSegundos { get; set; } = 120;

    /// <summary>
    /// Intentos de tomar el candado (el primero incluido) antes de responder <c>SERVICIO_OCUPADO</c> (503). Solo se reintenta la
    /// toma del candado, nunca la operación: entre intentos la transacción se deshace sin haber escrito nada. Por defecto 3.
    /// </summary>
    [Range(1, 5)]
    public int Intentos { get; set; } = 3;

    /// <summary>Pausa base entre intentos (se duplica en cada uno, con variación aleatoria de hasta la mitad). Por defecto 200 ms.</summary>
    [Range(0, 5_000)]
    public int PausaBaseMilisegundos { get; set; } = 200;
}
