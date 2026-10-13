using System.Text.Json.Serialization;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cubicacion.DTOs;

/// <summary>
/// Ítem de carga de "Calcular cubicaje" (<c>POST /web/cubicacion</c>, HU-023.2, #157): un bulto o grupo de bultos idénticos, con medidas
/// en <b>centímetros</b> y peso por unidad en <b>kilogramos</b>. El tipo de mercancía va por <paramref name="CodigoMercancia"/> (catálogo
/// del cubicador). Mientras el frontend no tenga el desplegable, sin código se usa <paramref name="Descripcion"/>: Cotifly la resuelve al
/// tipo más parecido del catálogo (<c>clsSugeridorMercancia</c>).
/// </summary>
/// <param name="CodigoMercancia">Código del catálogo del cubicador (<c>GET /web/tipos-mercancia</c>); gana sobre la descripción.</param>
/// <param name="Descripcion">Texto libre de la mercancía; solo se usa si no viene el código.</param>
/// <param name="Cantidad">Cuántos bultos idénticos con estas medidas.</param>
/// <param name="LargoCm">Largo en centímetros.</param>
/// <param name="AnchoCm">Ancho en centímetros.</param>
/// <param name="AltoCm">Alto en centímetros.</param>
/// <param name="PesoKg">Peso en kilogramos de cada bulto (opcional).</param>
public sealed record clsItemCubicacion(
    string? CodigoMercancia, string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

/// <summary>
/// Respuesta de "Calcular cubicaje" (<c>POST /web/cubicacion</c>, HU-023.2, #157): lo que respondió el cubicador a una corrida de prueba
/// (<c>PRUEBA-&lt;guid&gt;</c>) que Cotifly no guarda. Una <b>lista de vehículos</b> (flota mixta: pueden ser de tipos distintos), el
/// resultado con su motivo, la marca de condicionada, las mercancías que la condicionan, las advertencias, la extradimensión y el código
/// con el que viajó cada ítem. Ya no trae clase de viaje ni clase de contenedor: el cubicador no las sugiere.
/// </summary>
/// <param name="Resultado"><c>ASIGNADO</c> o <c>SIN_SELECCION_AUTOMATICA</c>.</param>
/// <param name="MotivoSinSeleccion">Con <c>SIN_SELECCION_AUTOMATICA</c>, uno de los cuatro motivos; si no, <c>null</c>.</param>
/// <param name="Condicionada">Alguna mercancía requiere aprobación (la ruta iría a la Mesa de Costeo).</param>
/// <param name="Vehiculos">Los vehículos recomendados, en el orden del cubicador; vacía sin selección.</param>
/// <param name="MercanciasCondicionadas">Las mercancías que condicionan la ruta.</param>
/// <param name="Advertencias">Advertencias del cubicador.</param>
/// <param name="Extradimension">Extradimensión informada por el cubicador, o <c>null</c>.</param>
/// <param name="Mercancias">El código con el que viajó cada ítem, en el orden de la petición.</param>
/// <param name="Sugerencia">
/// <b>Plan B, apagado por defecto</b> (<c>Cubicacion:IncluirSugerenciaLegada</c>): la forma anterior derivada del primer vehículo, para un
/// frontend que todavía no pinte <see cref="Vehiculos"/>. Sin la bandera, o sin vehículos, no se serializa.
/// </param>
public sealed record clsResultadoCubicacion(
    string Resultado,
    string? MotivoSinSeleccion,
    bool Condicionada,
    IReadOnlyList<clsVehiculoCubicacion> Vehiculos,
    IReadOnlyList<clsMercanciaCondicionada> MercanciasCondicionadas,
    IReadOnlyList<clsAdvertenciaCubicador> Advertencias,
    clsExtradimensionCubicador? Extradimension,
    IReadOnlyList<clsMercanciaCubicada> Mercancias,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] clsSugerenciaVehiculoLegada? Sugerencia = null);

/// <summary>
/// Forma anterior de <c>sugerencia</c> (<c>SugerenciaVehiculo</c> de la cubicación de Nexo, <c>deprecated</c>), derivada del primer vehículo
/// del cubicador: <paramref name="UnidadId"/> y <paramref name="ConfigVehiculo"/> = código del vehículo, <paramref name="UnidadNombre"/> = su
/// nombre, <paramref name="Banda"/> = <c>"cubicador"</c>, <paramref name="FactorUtilizacion"/> = el mayor de los dos porcentajes de llenado
/// sobre 100, <paramref name="Carroceria"/> = código de la carrocería; las clases de viaje y de contenedor siempre <c>null</c> (el cubicador
/// no las sugiere). Solo con <c>Cubicacion:IncluirSugerenciaLegada</c>.
/// </summary>
public sealed record clsSugerenciaVehiculoLegada(
    string UnidadId,
    string UnidadNombre,
    string Banda,
    decimal FactorUtilizacion,
    string? ConfigVehiculo,
    string? Carroceria,
    string? ClaseViaje,
    string? ClaseContenedor)
{
    /// <summary>Valor de <see cref="Banda"/>.</summary>
    public const string BandaCubicador = "cubicador";

    /// <summary>La sugerencia legada del primer vehículo, o <c>null</c> sin vehículos.</summary>
    public static clsSugerenciaVehiculoLegada? Desde(IReadOnlyList<clsVehiculoCubicacion> vehiculos)
    {
        ArgumentNullException.ThrowIfNull(vehiculos);
        if (vehiculos.Count == 0)
        {
            return null;
        }

        var v = vehiculos[0];
        return new clsSugerenciaVehiculoLegada(
            v.CodigoVehiculo, v.NombreVehiculo, BandaCubicador,
            Math.Round(Math.Max(v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso) / 100m, 4),
            v.CodigoVehiculo, v.CodigoCarroceria, null, null);
    }
}

/// <summary>
/// Un vehículo recomendado por el cubicador. Los nombres salen de los catálogos del cubicador (<c>GET /tipos-vehiculo</c> y
/// <c>/tipos-carroceria</c>, con caché); si el catálogo no responde o no trae el código, el nombre del vehículo es el que mandó el
/// cubicador en la respuesta o el código, y el de la carrocería, el código.
/// </summary>
public sealed record clsVehiculoCubicacion(
    string CodigoVehiculo,
    string NombreVehiculo,
    string CodigoCarroceria,
    string NombreCarroceria,
    decimal PesoTotalKg,
    decimal VolumenTotalM3,
    decimal PorcentajeLlenadoVolumen,
    decimal PorcentajeLlenadoPeso);

/// <summary>
/// El tipo de mercancía con el que viajó un ítem al cubicador. <paramref name="Sugerida"/> es <c>true</c> si se resolvió por parecido
/// desde <paramref name="Descripcion"/> (el comercial debería confirmarlo); <paramref name="Nombre"/> es el del catálogo (<c>null</c> si
/// el ítem traía el código y no hubo que leer el catálogo).
/// </summary>
public sealed record clsMercanciaCubicada(string CodigoMercancia, string? Nombre, string? Descripcion, bool Sugerida);
