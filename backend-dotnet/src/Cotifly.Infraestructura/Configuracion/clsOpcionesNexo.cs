using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Base URL de Nexo (sección <c>Nexo</c>, variable <c>Nexo__UrlBase</c>), consumida por la búsqueda de clientes
/// (HU-005.2, ADR-005), municipios, catálogos de flete, cubicación y el costeo (ADR-015). Sin autenticación propia: cada petición manda <c>X-Portal-clsUsuario-Id</c> (el <c>idUsuarioExterno</c>
/// del comercial), no un token — Nexo resuelve el rol contra el mismo almacén corporativo que ClientePro.
/// Sin <see cref="UrlBase"/> (vacía, valor por defecto), <c>clsFabricaBuscadorClientes</c> usa
/// <see cref="Clientes.Services.clsBuscadorClientesNoDisponible"/>: no hay <c>[Required]</c> porque Nexo aún no es
/// obligatorio en todos los ambientes.
/// </summary>
public sealed class clsOpcionesNexo : IValidatableObject
{
    public const string Seccion = "Nexo";

    /// <summary>
    /// Raíz del servicio Nexo, <b>sin</b> <c>/api</c> ni barra final, ej. <c>https://nexo.boterosoto.com.co</c>: todas las rutas del
    /// contrato de Nexo empiezan por <c>/api/...</c> y el código las agrega (ADR-015 §8).
    /// </summary>
    public string? UrlBase { get; set; }

    /// <summary>
    /// ¿Cada línea de <c>POST /api/captura-solicitudes</c> lleva <c>grupoRutaId</c> (el id de la ruta de Cotifly, GUID en texto)? Es la
    /// petición N1 a Nexo (#134), que Nexo implementó como <c>GrupoRutaId</c> (S5-HU-18.3, #275) en <c>clsCapturaLineaRequest</c> y que
    /// devuelve en cada línea de <c>GET /api/solicitudes/{id}</c> (HU-023.4, #159). Antes se llamaba <c>Nexo:EnviarReferenciaRuta</c> (y así
    /// la nombra el texto de Nexo #297); Cotifly la renombró a <c>Nexo:EnviarGrupoRutaId</c>. HU-023 (#176): Nexo ya lo desplegó, así
    /// que por defecto es <c>true</c> y la bandera queda solo como interruptor de emergencia. Una ruta con un solo camión ("línea suelta")
    /// lleva <c>grupoRutaId = null</c> (contrato con Nexo §3.3); con varios, todas sus líneas llevan el mismo GUID y van contiguas.
    /// </summary>
    public bool EnviarGrupoRutaId { get; set; } = true;

    /// <summary>
    /// ¿Viaja a Nexo el camión del cubicador (HU-023.4, #159)? Con <c>true</c>, cada línea lleva <c>idRecomendacionVehiculo</c> (el camión
    /// de la recomendación, S5-HU-18.2 de Nexo). HU-023 (#176): por defecto <c>true</c> (Nexo ya lo desplegó); la bandera queda como
    /// interruptor de emergencia. El código de mercancía no viaja nunca (contrato con Nexo #297).
    /// </summary>
    public bool EnviarDatosCubicador { get; set; } = true;

    /// <summary>
    /// <c>Nexo__EnviarCodigosCubicador</c>: ¿viajan los códigos de vehículo y carrocería del cubicador (<c>2S2</c>, <c>FURGON</c>...) en
    /// <c>tipoVehiculo</c>/<c>carroceria</c> de la línea de un camión del cubicador? Tres modos (<see cref="ModoCodigosCubicador"/>):
    /// <list type="bullet">
    /// <item><c>solo_conocidos</c> (<b>por defecto</b>, HU-014 #188, decisión del usuario del 2026-10-19: "enviar solo códigos
    /// conocidos"): el vehículo viaja solo si su código está en <c>GET /api/catalogos/tipos-vehiculo</c> de Nexo (con la equivalencia de
    /// <see cref="Dominio.Cotizaciones.clsVehiculoLineaSolicitud.MismoCodigo"/>), escrito como en el catálogo de Nexo; la carrocería, solo si
    /// el vehículo viaja y está en la lista de carrocerías de Nexo. Si no, la línea va sin vehículo ni carrocería (no nace en cuarentena)
    /// y la resuelve la Mesa de Costeo. Sin catálogo de Nexo (caído y sin copia de respaldo) ningún código es conocido.</item>
    /// <item><c>siempre</c>: viajan todos (un código que Nexo no conoce nace en cuarentena, S5-HU-18.4); para cuando Nexo tenga la bandeja
    /// de cuarentena (S5-HU-19.3).</item>
    /// <item><c>nunca</c>: ninguno viaja (interruptor de emergencia; era el comportamiento por defecto entre la decisión anterior del
    /// 2026-10-19 y esta).</item>
    /// </list>
    /// Compatibilidad con la bandera booleana anterior: <c>true</c> = <c>siempre</c>; <c>false</c> = <c>solo_conocidos</c> (la intención
    /// de <c>false</c> era no generar cuarentenas, y <c>solo_conocidos</c> tampoco las genera). Vacía = <c>solo_conocidos</c>. Cualquier otro
    /// valor impide arrancar (<see cref="Validate"/>). No afecta al vehículo digitado del respaldo (<c>Captura:AceptarVehiculoCliente</c>,
    /// sin <c>idRecomendacionVehiculo</c>): la captura ya exige que sea un código de Nexo y viaja siempre.
    /// </summary>
    public string? EnviarCodigosCubicador { get; set; }

    /// <summary>El modo de <see cref="EnviarCodigosCubicador"/>; un valor inválido se lee como el modo por defecto (y no deja arrancar).</summary>
    public ModoCodigosCubicador ModoCodigosCubicador => LeerModo(EnviarCodigosCubicador) ?? ModoCodigosCubicador.SoloConocidos;

    /// <summary>
    /// El modo escrito en <paramref name="valor"/> (sin distinguir mayúsculas, con <c>_</c>, <c>-</c> o espacio): <c>siempre</c> o
    /// <c>true</c>; <c>solo_conocidos</c> o <c>false</c>; <c>nunca</c>; vacío = <c>solo_conocidos</c>. <c>null</c> si no es ninguno.
    /// </summary>
    public static ModoCodigosCubicador? LeerModo(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return ModoCodigosCubicador.SoloConocidos;
        }

        var limpio = valor.Trim().Replace('-', '_').Replace(' ', '_');
        return limpio switch
        {
            _ when Igual(limpio, "siempre") || Igual(limpio, bool.TrueString) => ModoCodigosCubicador.Siempre,
            _ when Igual(limpio, "solo_conocidos") || Igual(limpio, "soloconocidos") || Igual(limpio, bool.FalseString) => ModoCodigosCubicador.SoloConocidos,
            _ when Igual(limpio, "nunca") => ModoCodigosCubicador.Nunca,
            _ => null,
        };

        static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validación al arrancar (<c>ValidateDataAnnotations</c>): <see cref="EnviarCodigosCubicador"/> debe ser un modo conocido.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LeerModo(EnviarCodigosCubicador) is null)
        {
            yield return new ValidationResult(
                $"Nexo:EnviarCodigosCubicador = '{EnviarCodigosCubicador}' no es válido: use siempre, solo_conocidos o nunca (true = siempre, false = solo_conocidos).",
                [nameof(EnviarCodigosCubicador)]);
        }
    }

    /// <summary>
    /// Minutos que se reutiliza en memoria el catálogo de municipios de Nexo (HU-005.13: cambia muy poco y el agente lo consulta
    /// en cada <c>resolver_municipio</c>). Solo se guardan respuestas exitosas; <c>0</c> desactiva la caché. Por defecto 60.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheMunicipios { get; set; } = 60;

    /// <summary>
    /// Minutos que se reutilizan en memoria los catálogos maestros de Nexo (<c>GET /api/catalogos/*</c>, HU-013.10) sin volver a
    /// consultarlos. <c>0</c> desactiva la caché (y la copia de respaldo). Por defecto 30.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheCatalogos { get; set; } = 30;

    /// <summary>
    /// Horas que la última copia de los catálogos maestros sirve de respaldo si Nexo no responde al renovarla (HU-013.10, G2). Pasado
    /// ese límite, sin Nexo, <c>GET /web/catalogos-flete</c> y <c>POST /web/solicitudes-costeo</c> responden
    /// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>. <c>0</c> desactiva el respaldo. Por defecto 24.
    /// </summary>
    [Range(0, 168)]
    public int HorasCatalogosVencidos { get; set; } = 24;
}

/// <summary>Modos de <see cref="clsOpcionesNexo.EnviarCodigosCubicador"/> (HU-014, #188).</summary>
public enum ModoCodigosCubicador
{
    /// <summary><c>solo_conocidos</c> (por defecto): solo los códigos que están en el catálogo de Nexo.</summary>
    SoloConocidos,

    /// <summary><c>siempre</c> (antes <c>true</c>): todos, aunque Nexo no los conozca (cuarentena).</summary>
    Siempre,

    /// <summary><c>nunca</c>: ninguno (interruptor de emergencia).</summary>
    Nunca,
}
