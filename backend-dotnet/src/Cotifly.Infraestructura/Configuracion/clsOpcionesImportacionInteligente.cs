using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Importación inteligente del Excel con NEXO (HU-044.1, #252, ADR-028), sección <c>ImportacionInteligente</c> (variables
/// <c>ImportacionInteligente__Habilitada</c>, <c>ImportacionInteligente__TimeoutSegundos</c> y
/// <c>ImportacionInteligente__TimeoutLecturaSegundos</c> y <c>ImportacionInteligente__LimiteRespuestaMegabytes</c>). Se valida al arrancar: un tiempo fuera de rango impide arrancar. Apagada, o sin
/// <c>Nexo:UrlBase</c>, Cotifly registra <see cref="Licitaciones.Services.clsImportacionExcelNoDisponible"/> y no llama a NEXO.
/// </summary>
public sealed class clsOpcionesImportacionInteligente
{
    public const string Seccion = "ImportacionInteligente";

    /// <summary>Mínimo de <see cref="TimeoutLecturaSegundos"/>: NEXO hace hasta 3 intentos de IA de 60 s en la lectura.</summary>
    public const int MinimoTimeoutLecturaSegundos = 180;

    /// <summary>
    /// Valor por defecto de <see cref="TimeoutLecturaSegundos"/>: el mínimo más un margen para que NEXO, tras agotar sus intentos de IA, alcance
    /// a ejecutar la receta o la lectura sin IA y responda 200 con <c>iaUsada = false</c> en lugar de que Cotifly corte antes.
    /// </summary>
    public const int TimeoutLecturaPorDefectoSegundos = 240;

    /// <summary>
    /// Interruptor de la importación inteligente. Por defecto <c>true</c>: con <c>Nexo:UrlBase</c> configurada está encendida y la bandera
    /// queda como interruptor de emergencia (por ejemplo, si el gasto de IA de NEXO se dispara).
    /// </summary>
    public bool Habilitada { get; set; } = true;

    /// <summary>
    /// Tiempo máximo de cada intento contra NEXO en <c>sugerir-mapeo</c>, <c>aplicar-mapeo</c>, <c>normalizar-valores</c>, las plantillas y el
    /// diccionario. Por defecto 90 s (la sugerencia y la normalización llaman a la IA una vez).
    /// </summary>
    [Range(10, 600)]
    public int TimeoutSegundos { get; set; } = 90;

    /// <summary>
    /// Tiempo máximo de <c>leer-estructura</c> y de <c>PUT recetas-lectura</c> (un solo intento: no se reintentan). Por defecto
    /// <see cref="TimeoutLecturaPorDefectoSegundos"/> y como mínimo <see cref="MinimoTimeoutLecturaSegundos"/>: NEXO hace hasta 3 intentos de IA
    /// de 60 s y luego ejecuta la receta.
    /// </summary>
    [Range(MinimoTimeoutLecturaSegundos, 900)]
    public int TimeoutLecturaSegundos { get; set; } = TimeoutLecturaPorDefectoSegundos;

    /// <summary>
    /// Tamaño máximo del cuerpo de una respuesta de NEXO, en MB. Por defecto 20: las tablas de <c>leer-estructura</c> y las líneas de
    /// <c>aplicar-mapeo</c> salen de un Excel de hasta 10 MB comprimido, que en JSON ocupa más. Una respuesta mayor → <c>NEXO_NO_DISPONIBLE</c>.
    /// </summary>
    [Range(1, 200)]
    public int LimiteRespuestaMegabytes { get; set; } = 20;
}
