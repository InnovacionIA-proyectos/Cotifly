using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Base URL de Nexo (sección <c>Nexo</c>, variable <c>Nexo__UrlBase</c>), consumida por la búsqueda de clientes
/// (ADR-005), municipios, catálogos de flete, cubicación y el costeo (ADR-015). Sin autenticación propia: cada petición manda <c>X-Portal-clsUsuario-Id</c> (el <c>idUsuarioExterno</c>
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
    /// ¿Cada línea de <c>POST /api/captura-solicitudes</c> lleva <c>grupoRutaId</c> (el id de la ruta de Cotifly, GUID en texto)? Nexo
    /// lo recibe como <c>GrupoRutaId</c> en <c>clsCapturaLineaRequest</c> y lo devuelve en cada línea de <c>GET /api/solicitudes/{id}</c>.
    /// Por defecto es <c>true</c>; la bandera queda solo como interruptor de emergencia. Una ruta con un solo camión ("línea suelta")
    /// lleva <c>grupoRutaId = null</c> (contrato con Nexo §3.3); con varios, todas sus líneas llevan el mismo GUID y van contiguas.
    /// </summary>
    public bool EnviarGrupoRutaId { get; set; } = true;

    /// <summary>
    /// ¿Viaja a Nexo el camión del cubicador? Con <c>true</c>, cada línea lleva <c>idRecomendacionVehiculo</c> (el camión
    /// de la recomendación). Por defecto <c>true</c>; la bandera queda como interruptor de emergencia. El código de mercancía no viaja
    /// nunca (contrato con Nexo).
    /// </summary>
    public bool EnviarDatosCubicador { get; set; } = true;

    /// <summary>
    /// <c>Nexo__EnviarCodigosCubicador</c>: ¿viajan los códigos de vehículo y carrocería del cubicador (<c>2S2</c>, <c>FURGON</c>...) en
    /// <c>tipoVehiculo</c>/<c>carroceria</c> de la línea de un camión del cubicador? Antes de decidir, el código se resuelve contra el
    /// catálogo oficial de Nexo (<c>GET /api/catalogos/tipos-vehiculo</c>, con la equivalencia de
    /// <see cref="Dominio.Cotizaciones.clsVehiculoLineaSolicitud.MismoCodigo"/>) y, si no está, contra el diccionario de alias de Nexo
    /// (<c>C2</c> → <c>2</c>); lo resuelto viaja escrito como en Nexo y la línea se autocostea. Tres modos
    /// (<see cref="ModoCodigosCubicador"/>):
    /// <list type="bullet">
    /// <item><c>siempre</c> (<b>por defecto</b>, porque Nexo tiene la bandeja de normalización): viajan todos; el resuelto con su
    /// código oficial y el que ni el catálogo ni el alias conocen, tal cual
    /// (Nexo lo pone en cuarentena y lo normaliza en su bandeja).</item>
    /// <item><c>solo_conocidos</c>: el vehículo viaja solo si se resolvió (catálogo o alias);
    /// la carrocería, solo si el vehículo viaja y está en la lista de carrocerías de Nexo. Si no, la línea va sin vehículo ni carrocería (no
    /// nace en cuarentena) y la resuelve la Mesa de Costeo.</item>
    /// <item><c>nunca</c>: ninguno viaja (interruptor de emergencia; ni siquiera se consulta el alias).</item>
    /// </list>
    /// Sin catálogo ni diccionario de Nexo (caídos y sin copia) el envío no falla: sigue con el modo configurado sin resolver nada (con
    /// <c>siempre</c>, los códigos viajan tal cual). Compatibilidad con la bandera booleana anterior: <c>true</c> = <c>siempre</c> (ya es el
    /// valor por defecto, así que un <c>true</c> que quede en Dokploy no es un riesgo); <c>false</c> = <c>solo_conocidos</c> (la intención
    /// de <c>false</c> era no generar cuarentenas, y <c>solo_conocidos</c> tampoco las genera). Vacía = <c>siempre</c>. Cualquier otro valor
    /// impide arrancar (<see cref="Validate"/>). El vehículo digitado del respaldo (<c>Captura:AceptarVehiculoCliente</c>, sin
    /// <c>idRecomendacionVehiculo</c>) no depende del modo: la captura exige que sea un código de Nexo o un alias que resuelve a uno y viaja
    /// siempre, resuelto.
    /// </summary>
    public string? EnviarCodigosCubicador { get; set; }

    /// <summary>El modo de <see cref="EnviarCodigosCubicador"/>; un valor inválido se lee como el modo por defecto (y no deja arrancar).</summary>
    public ModoCodigosCubicador ModoCodigosCubicador => LeerModo(EnviarCodigosCubicador) ?? ModoCodigosCubicador.Siempre;

    /// <summary>
    /// El modo escrito en <paramref name="valor"/> (sin distinguir mayúsculas, con <c>_</c>, <c>-</c> o espacio): <c>siempre</c> o
    /// <c>true</c>; <c>solo_conocidos</c> o <c>false</c>; <c>nunca</c>; vacío = <c>siempre</c>. <c>null</c> si no es ninguno.
    /// </summary>
    public static ModoCodigosCubicador? LeerModo(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return ModoCodigosCubicador.Siempre;
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
    /// Minutos que se reutiliza en memoria el catálogo de municipios de Nexo (cambia muy poco; lo consultan
    /// <c>GET /web/municipios</c> y la validación de la captura y de licitaciones). Solo se guardan respuestas exitosas; <c>0</c>
    /// desactiva la caché. Por defecto 60.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheMunicipios { get; set; } = 60;

    /// <summary>
    /// Minutos que se reutilizan en memoria los catálogos maestros de Nexo (<c>GET /api/catalogos/*</c>) sin volver a
    /// consultarlos. <c>0</c> desactiva la caché (y la copia de respaldo). Por defecto 30.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheCatalogos { get; set; } = 30;

    /// <summary>
    /// Horas que la última copia de los catálogos maestros sirve de respaldo si Nexo no responde al renovarla. Pasado
    /// ese límite, sin Nexo, <c>GET /web/catalogos-flete</c> y <c>POST /web/solicitudes-costeo</c> responden
    /// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>. <c>0</c> desactiva el respaldo. Por defecto 24.
    /// </summary>
    [Range(0, 168)]
    public int HorasCatalogosVencidos { get; set; } = 24;
}

/// <summary>Modos de <see cref="clsOpcionesNexo.EnviarCodigosCubicador"/>.</summary>
public enum ModoCodigosCubicador
{
    /// <summary><c>solo_conocidos</c>: solo los códigos que el catálogo o el diccionario de alias de Nexo resuelven.</summary>
    SoloConocidos,

    /// <summary><c>siempre</c> (por defecto; también <c>true</c>): todos, resueltos si se puede; los demás, a cuarentena en Nexo.</summary>
    Siempre,

    /// <summary><c>nunca</c>: ninguno (interruptor de emergencia).</summary>
    Nunca,
}
