using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Límites del loop del agente que consume el runner (sección <c>IA</c>; el proveedor, el modelo y la clave los lee
/// Infraestructura en <c>clsOpcionesIA</c> de la misma sección). Port de <c>llm_max_tokens</c> y <c>agente_max_iteraciones</c>.
/// </summary>
public sealed class clsOpcionesAgente
{
    public const string Seccion = "IA";

    /// <summary>Tope de tokens de salida por respuesta del modelo. Por defecto 4096.</summary>
    [Range(256, 200_000)]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Llamadas al modelo con herramientas por turno antes de cortar (evita bucles). Por defecto 12.</summary>
    [Range(1, 50)]
    public int MaxIteraciones { get; set; } = 12;
}

/// <summary>
/// Sesiones de conversación (sección <c>Sesiones</c>). Port de <c>sesion_limite_tokens</c>, <c>sesion_ttl_horas</c> y
/// <c>sesion_barrido_seg</c> de <c>shared/config.py</c>.
/// </summary>
public sealed class clsOpcionesSesiones
{
    public const string Seccion = "Sesiones";

    /// <summary>Tokens (~4 caracteres por token) del historial que se entrega al modelo; siempre se conserva el primer mensaje. Por defecto 6000.</summary>
    [Range(200, 1_000_000)]
    public int LimiteTokens { get; set; } = 6000;

    /// <summary>Horas de inactividad tras las que el barrido archiva la sesión. Por defecto 24.</summary>
    [Range(1, 24 * 90)]
    public int TtlHoras { get; set; } = 24;

    /// <summary>Minutos entre barridos de sesiones inactivas; 0 lo desactiva. Por defecto 60 (el Python: 3600 s).</summary>
    [Range(0, 24 * 60)]
    public int BarridoMinutos { get; set; } = 60;

    /// <summary>
    /// Segundos que un turno espera a que termine otro de la misma sesión antes de responder "todavía estoy respondiendo"
    /// (candado por sesión). Por defecto 3.
    /// </summary>
    [Range(0, 60)]
    public int EsperaTurnoSegundos { get; set; } = 3;
}
