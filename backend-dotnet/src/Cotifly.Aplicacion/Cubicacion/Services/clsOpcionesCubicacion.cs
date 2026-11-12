using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>Opciones de "Calcular cubicaje" (sección <c>Cubicacion</c>).</summary>
public sealed class clsOpcionesCubicacion
{
    public const string Seccion = "Cubicacion";

    /// <summary>
    /// <c>Cubicacion__ParecidoMinimoMercancia</c> (por defecto <see cref="clsSugeridorMercancia.ParecidoMinimo"/>, más de 0,5 y a lo sumo
    /// 1): parecido mínimo para resolver una <c>descripcion</c> sin código al tipo de mercancía del catálogo.
    /// </summary>
    [Range(0.501, 1.0)]
    public double ParecidoMinimoMercancia { get; set; } = clsSugeridorMercancia.ParecidoMinimo;

    /// <summary>
    /// <c>Cubicacion__MargenAmbiguedadMercancia</c> (por defecto <see cref="clsSugeridorMercancia.MargenAmbiguedad"/>, de 0 a 0,5): si el
    /// segundo mejor tipo queda a menos de este margen del mejor, la descripción es ambigua y no se resuelve.
    /// </summary>
    [Range(0.0, 0.5)]
    public double MargenAmbiguedadMercancia { get; set; } = clsSugeridorMercancia.MargenAmbiguedad;

    /// <summary>
    /// <c>Cubicacion__IncluirSugerenciaLegada</c> (por defecto <c>false</c>): <b>plan B del corte del 16 de octubre</b>, solo si el frontend
    /// no alcanza a pintar la lista de vehículos. Encendida, la respuesta de <c>POST /web/cubicacion</c> agrega <c>sugerencia</c> con la
    /// forma anterior (<c>SugerenciaVehiculo</c>) derivada del <b>primer</b> vehículo; sin vehículos (sin selección) viene <c>null</c>.
    /// Apagada, el campo no aparece. Se retira cuando el frontend use <c>vehiculos[]</c>.
    /// </summary>
    public bool IncluirSugerenciaLegada { get; set; }
}
