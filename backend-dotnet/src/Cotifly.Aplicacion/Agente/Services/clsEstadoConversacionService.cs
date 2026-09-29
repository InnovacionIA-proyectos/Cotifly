using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Resultado de reconocer el mensaje del comercial al empezar el turno.</summary>
/// <param name="Estado">Estado con la selección de un cliente candidato, si la hubo.</param>
/// <param name="Confirmado">True si el mensaje confirma la previsualización mostrada (habilita el PDF y el correo del turno).</param>
public sealed record clsMensajeReconocido(clsEstadoConversacion Estado, bool Confirmado);

/// <summary>
/// Único que cambia el estado de la conversación (ADR-004 §3), de forma determinista: (a) con la salida de una herramienta que
/// terminó bien y (b) con una confirmación explícita del comercial que reconoce el runner. El LLM nunca escribe el estado.
/// </summary>
public interface IClsEstadoConversacionService
{
    /// <summary>Estado después de la herramienta. Un resultado con error no cambia nada.</summary>
    /// <param name="argumentos">Argumentos con que se ejecutó (ya con lo que inyectó el filtro): textos libres como la ruta.</param>
    clsEstadoConversacion Aplicar(clsEstadoConversacion estado, clsResultadoHerramienta resultado, IReadOnlyDictionary<string, object?> argumentos);

    /// <summary>Reconoce en el mensaje del comercial la elección de un cliente candidato o la confirmación de la previsualización.</summary>
    clsMensajeReconocido ReconocerMensaje(clsEstadoConversacion estado, string mensaje);

    /// <summary>
    /// Estado de una nueva versión (auditoría de #12, I2): la cotización de origen (ya validada como del comercial) con su cliente, línea
    /// y números; <c>generar_pdf</c> crea entonces <c>{folio base}-vN</c>. Sin previsualización pendiente: el comercial revisa y confirma.
    /// </summary>
    clsEstadoConversacion SembrarDesdeCotizacion(Dominio.Cotizaciones.clsCotizacion origen, string? lineaNegocio);
}

/// <summary>Implementación de <see cref="IClsEstadoConversacionService"/>.</summary>
public sealed partial class clsEstadoConversacionService : IClsEstadoConversacionService
{
    private static readonly string[] _ordinales = ["primero", "segundo", "tercero", "cuarto", "quinto"];
    private static readonly string[] _ordinalesFemeninos = ["primera", "segunda", "tercera", "cuarta", "quinta"];

    /// <summary>
    /// Palabras (en su <c>Clave</c>: sin tildes ni mayúsculas) que puede traer un mensaje que elige un municipio candidato además de
    /// los nombres de los candidatos (HU-005.13): artículos, cortesía, confirmación, el campo y la posición.
    /// </summary>
    private static readonly string[] _palabrasDeRelleno =
    [
        "el", "la", "los", "las", "lo", "de", "del", "en", "es", "era", "seria", "sea", "ese", "esa", "este", "esta", "eso", "misma",
        "mismo", "que", "si", "no", "ok", "okay", "dale", "listo", "claro", "perfecto", "correcto", "exacto", "obvio", "bueno", "pues",
        "mejor", "elijo", "escojo", "quiero", "confirmo", "gracias", "por", "favor", "porfa", "opcion", "numero", "municipio", "ciudad",
        "departamento", "origen", "destino", "primero", "primera", "segundo", "segunda", "tercero", "tercera", "cuarto", "cuarta",
        "quinto", "quinta", "ultimo", "ultima", "uno", "dos", "tres", "cuatro", "cinco", "hola",
    ];

    public clsEstadoConversacion Aplicar(clsEstadoConversacion estado, clsResultadoHerramienta resultado, IReadOnlyDictionary<string, object?> argumentos)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(resultado);
        ArgumentNullException.ThrowIfNull(argumentos);

        if (!resultado.EsExitoso)
        {
            if (resultado.Herramienta == clsNombresHerramientas.ResolverMunicipio)
            {
                // Sin catálogo de municipios (HU-005.13): la cotización sigue con el texto libre del comercial, sin municipio verificado.
                return resultado.Error!.Codigo == clsCodigosError.MunicipiosNoDisponible
                       && clsArgumentosHerramienta.CampoMunicipio(Texto(argumentos, clsArgumentosHerramienta.Campo)) is { } campoLibre
                       && Texto(argumentos, clsArgumentosHerramienta.TextoLibre) is { } textoLibre
                    ? ConTextoLibre(estado, campoLibre, textoLibre)
                    : estado;
            }

            // Sin catálogo (ADR-005 §5): el texto del comercial queda solo como referencia visible, sin NIT.
            return resultado.Herramienta == clsNombresHerramientas.BuscarCliente
                   && resultado.Error!.Codigo == clsCodigosError.ClienteBusquedaNoDisponible
                   && !estado.ClienteConfirmado
                   && Texto(argumentos, "nombre") is { } referencia
                ? SinDocumentoSi(!string.Equals(estado.ClienteNombre, referencia, StringComparison.OrdinalIgnoreCase) || estado.NitCliente is not null, estado)
                    with { ClienteNombre = referencia, NitCliente = null, OrigenCliente = null, BpCliente = null }
                : estado;
        }

        return resultado.Detalle switch
        {
            clsResultadoBusquedaClientes busqueda => AplicarBusqueda(estado, busqueda),
            clsResolucionMunicipio resolucion => AplicarMunicipio(estado, resolucion, argumentos),
            clsSalidaTarifa tarifa => AplicarTarifa(estado, tarifa, argumentos),
            clsMargenResuelto margen => AplicarMargen(estado, margen),
            clsSalidaPrecio precio => AplicarPrecio(estado, precio),
            clsSalidaAjusteMargen ajuste => AplicarAjuste(estado, ajuste),
            clsSalidaPdf pdf => estado with
            {
                CotizacionId = pdf.CotizacionId,
                FolioEnProgreso = pdf.Folio,
                Version = clsDominioFolio.Version(pdf.Folio),
                ConfirmacionPendiente = false,
            },
            _ => estado,
        };
    }

    public clsEstadoConversacion SembrarDesdeCotizacion(Dominio.Cotizaciones.clsCotizacion origen, string? lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(origen);
        return clsEstadoConversacion.Vacio with
        {
            CotizacionId = origen.Id,
            FolioEnProgreso = origen.Folio,
            Version = origen.Version,
            NitCliente = origen.NitCliente,
            ClienteNombre = origen.ClienteNombre,
            OrigenCliente = origen.OrigenCliente,
            ClienteConfirmado = origen.NitCliente is not null,
            LineaNegocioId = origen.LineaNegocioId,
            LineaNegocio = lineaNegocio,
            Costo = origen.Costo,
            FuenteCosto = origen.Costo is null ? null : clsEstadoConversacion.FuenteCostoManual,
            CosteoId = origen.CosteoId,
            MargenPolitica = origen.MargenPolitica,
            MargenSugerido = origen.MargenPolitica,
            MargenAplicado = origen.MargenAplicado,
            OrigenMargen = origen.OrigenMargen,
            MetodoCalculo = origen.MetodoCalculo,
            PrecioFinal = origen.PrecioFinal,
            Moneda = origen.Moneda,
            ConfirmacionPendiente = false,
        };
    }

    public clsMensajeReconocido ReconocerMensaje(clsEstadoConversacion estado, string mensaje)
    {
        ArgumentNullException.ThrowIfNull(estado);
        var normalizado = Normalizar(mensaje);

        var clientePendiente = estado.CandidatosCliente.Count > 0 && !estado.ClienteConfirmado;
        var actual = estado;
        var eligio = false;
        if (clientePendiente)
        {
            var elegido = ElegirCandidato(estado.CandidatosCliente, mensaje ?? string.Empty, normalizado);
            if (elegido is not null)
            {
                actual = ConCliente(actual, elegido);
                eligio = true;
            }
        }

        if (actual.TieneMunicipiosPorConfirmar() && ElegirMunicipio(actual, normalizado, clientePendiente) is { } eleccion)
        {
            actual = ConMunicipio(actual, eleccion.Campo, eleccion.Municipio);
            eligio = true;
        }

        if (eligio)
        {
            // Elegir el cliente o el municipio no confirma el PDF: son confirmaciones distintas.
            return new clsMensajeReconocido(actual, false);
        }

        // Con un municipio por elegir no hay previsualización que confirmar (HU-005.13).
        var confirmado = estado.ConfirmacionPendiente && estado.PrecioFinal is not null && !estado.TieneMunicipiosPorConfirmar() && EsConfirmacion(normalizado);
        return new clsMensajeReconocido(estado, confirmado);
    }

    /// <summary>¿El mensaje (normalizado) es una confirmación explícita? ("Confirmo", "sí", "dale", "genera el PDF"...).</summary>
    public static bool EsConfirmacion(string? mensaje)
    {
        var texto = Normalizar(mensaje);
        return texto.Length > 0 && !ExpresionNegacion().IsMatch(texto) && ExpresionConfirmacion().IsMatch(texto);
    }

    private static clsEstadoConversacion AplicarBusqueda(clsEstadoConversacion estado, clsResultadoBusquedaClientes busqueda)
    {
        var candidatos = busqueda.Candidatos.Select(Candidato).ToList();
        if (busqueda.Exacta && candidatos.Count == 1)
        {
            return ConCliente(estado, candidatos[0]);
        }

        return SinDocumentoSi(estado.NitCliente is not null, estado) with
        {
            NitCliente = null,
            OrigenCliente = null,
            BpCliente = null,
            ClienteConfirmado = false,
            CandidatosCliente = candidatos,
        };
    }

    /// <summary>
    /// Cotización en curso descartada (auditoría de #12, F3/I3): si cambia el cliente, la línea o la ruta, la siguiente cotización
    /// es nueva (folio nuevo), no una versión <c>-vN</c> de la anterior.
    /// </summary>
    private static clsEstadoConversacion SinDocumentoSi(bool cambio, clsEstadoConversacion estado) =>
        cambio && (estado.CotizacionId is not null || estado.FolioEnProgreso is not null)
            ? estado with { CotizacionId = null, FolioEnProgreso = null, Version = null }
            : estado;

    private static bool Distinto(string? anterior, string? nuevo) =>
        anterior is not null && nuevo is not null && !string.Equals(Normalizar(anterior), Normalizar(nuevo), StringComparison.Ordinal);

    private static clsEstadoConversacion ConCliente(clsEstadoConversacion estado, clsCandidatoCliente cliente) =>
        SinDocumentoSi(estado.NitCliente != cliente.NitCliente, estado) with
    {
        NitCliente = cliente.NitCliente,
        ClienteNombre = cliente.Nombre,
        OrigenCliente = cliente.OrigenCliente,
        BpCliente = cliente.Bp,
        ClienteConfirmado = true,
        CandidatosCliente = [],
    };

    private static clsCandidatoCliente Candidato(clsClienteEncontrado encontrado) => new(
        encontrado.NitCliente,
        encontrado.Nombre,
        clsCodigoEnum.TryDesdeCodigo<OrigenCliente>(encontrado.OrigenCliente, out var origen) ? origen : OrigenCliente.Cliente,
        string.IsNullOrWhiteSpace(encontrado.Bp) ? null : encontrado.Bp.Trim());

    private static clsEstadoConversacion AplicarTarifa(clsEstadoConversacion estado, clsSalidaTarifa tarifa, IReadOnlyDictionary<string, object?> argumentos)
    {
        var cambioDeRuta = Distinto(estado.Origen, Texto(argumentos, "origen")) || Distinto(estado.Destino, Texto(argumentos, "destino"));
        var origen = Texto(argumentos, "origen") ?? estado.Origen;
        var destino = Texto(argumentos, "destino") ?? estado.Destino;
        var nuevo = SinDocumentoSi(cambioDeRuta, estado) with
        {
            Origen = origen,
            Destino = destino,

            // Si la ruta de la tarifa ya no es la del municipio verificado, el municipio deja de estar verificado (HU-005.13).
            OrigenMunicipio = estado.OrigenMunicipio is { } municipioOrigen && !Distinto(municipioOrigen.Canonico(), origen) ? municipioOrigen : null,
            DestinoMunicipio = estado.DestinoMunicipio is { } municipioDestino && !Distinto(municipioDestino.Canonico(), destino) ? municipioDestino : null,
            TipoVehiculo = Texto(argumentos, "tipoVehiculo") ?? estado.TipoVehiculo,
            TipoCarga = Texto(argumentos, "tipoCarga") ?? estado.TipoCarga,
            PesoKg = Numero(argumentos, "pesoKg") ?? estado.PesoKg,
        };

        return tarifa is { Encontrada: true, Costo: { } costo }
            ? nuevo with { Costo = costo, FuenteCosto = tarifa.Fuente, CosteoId = tarifa.Referencia, Moneda = tarifa.Moneda, PrecioFinal = null, ConfirmacionPendiente = false }
            : nuevo;
    }

    private static clsEstadoConversacion AplicarMunicipio(clsEstadoConversacion estado, clsResolucionMunicipio resolucion, IReadOnlyDictionary<string, object?> argumentos)
    {
        if (clsArgumentosHerramienta.CampoMunicipio(Texto(argumentos, clsArgumentosHerramienta.Campo)) is not { } campo)
        {
            return estado;
        }

        return resolucion.Coincidencia switch
        {
            CoincidenciaMunicipio.Unica => ConMunicipio(estado, campo, resolucion.Candidatos[0]),
            CoincidenciaMunicipio.Varias => ConCandidatosMunicipio(estado, campo, resolucion.Texto, resolucion.Candidatos),

            // Ninguno: no se inventa nada; solo se descartan los candidatos que hubiera de ese campo.
            _ => campo == clsArgumentosHerramienta.Origen ? estado with { CandidatosOrigen = [] } : estado with { CandidatosDestino = [] },
        };
    }

    /// <summary>Municipio verificado en el origen o el destino: texto canónico <c>Municipio, Departamento</c> y sin candidatos.</summary>
    private static clsEstadoConversacion ConMunicipio(clsEstadoConversacion estado, string campo, clsMunicipioResuelto municipio)
    {
        var esOrigen = campo == clsArgumentosHerramienta.Origen;
        var anterior = esOrigen ? estado.OrigenMunicipio : estado.DestinoMunicipio;
        var texto = esOrigen ? estado.Origen : estado.Destino;
        var mismo = anterior is not null
            ? anterior.Id == municipio.Id
            : texto is null
              || clsResolvedorMunicipios.Clave(texto) == clsResolvedorMunicipios.Clave(municipio.Municipio)
              || clsResolvedorMunicipios.Clave(texto) == clsResolvedorMunicipios.Clave(municipio.Canonico());
        var nuevo = SinRutaSi(!mismo, estado);
        return esOrigen
            ? nuevo with { Origen = municipio.Canonico(), OrigenMunicipio = municipio, CandidatosOrigen = [] }
            : nuevo with { Destino = municipio.Canonico(), DestinoMunicipio = municipio, CandidatosDestino = [] };
    }

    /// <summary>Texto ambiguo: queda el texto del comercial, sin municipio verificado, y los candidatos para que elija.</summary>
    private static clsEstadoConversacion ConCandidatosMunicipio(
        clsEstadoConversacion estado, string campo, string texto, IReadOnlyList<clsMunicipioResuelto> candidatos)
    {
        var esOrigen = campo == clsArgumentosHerramienta.Origen;
        var cambio = Distinto(esOrigen ? estado.Origen : estado.Destino, texto) || (esOrigen ? estado.OrigenMunicipio : estado.DestinoMunicipio) is not null;
        var nuevo = SinRutaSi(cambio, estado);
        return esOrigen
            ? nuevo with { Origen = texto, OrigenMunicipio = null, CandidatosOrigen = candidatos }
            : nuevo with { Destino = texto, DestinoMunicipio = null, CandidatosDestino = candidatos };
    }

    /// <summary>Sin catálogo de municipios: el texto libre del comercial, sin municipio verificado ni candidatos.</summary>
    private static clsEstadoConversacion ConTextoLibre(clsEstadoConversacion estado, string campo, string texto)
    {
        var esOrigen = campo == clsArgumentosHerramienta.Origen;
        var nuevo = SinRutaSi(Distinto(esOrigen ? estado.Origen : estado.Destino, texto), estado);
        return esOrigen
            ? nuevo with { Origen = texto, OrigenMunicipio = null, CandidatosOrigen = [] }
            : nuevo with { Destino = texto, DestinoMunicipio = null, CandidatosDestino = [] };
    }

    /// <summary>
    /// Cambió el origen o el destino (HU-005.13): el costo, el precio y la previsualización eran de la ruta anterior y la cotización
    /// en curso deja de ser una versión de la anterior.
    /// </summary>
    private static clsEstadoConversacion SinRutaSi(bool cambio, clsEstadoConversacion estado) =>
        cambio
            ? SinDocumentoSi(true, estado) with { Costo = null, FuenteCosto = null, CosteoId = null, PrecioFinal = null, ConfirmacionPendiente = false }
            : estado;

    /// <summary>
    /// Elige un municipio candidato por el mensaje del comercial (HU-005.13). Nunca elige si:
    /// <list type="bullet">
    /// <item>el mensaje niega ("no es el de Nariño");</item>
    /// <item>trae alguna palabra que no es de los candidatos (municipio, departamento o país), del campo, de la posición ni de
    /// relleno: "va para medellin antioquia" o "el cliente es de antioquia" no eligen la de Antioquia; el modelo vuelve a llamar a
    /// <c>resolver_municipio</c>;</item>
    /// <item>nombra solo el campo que no está pendiente ("el destino..." con el origen por elegir);</item>
    /// <item>con el origen y el destino pendientes y sin nombrar el campo, el departamento coincide en las dos listas.</item>
    /// </list>
    /// Elige por el departamento (el más largo si uno contiene al otro: "Valle del Cauca" sobre "Cauca") o, con una sola lista en
    /// juego y sin clientes candidatos, por posición ("2", "la segunda") o por un "sí" con un solo candidato.
    /// </summary>
    private static (string Campo, clsMunicipioResuelto Municipio)? ElegirMunicipio(
        clsEstadoConversacion estado, string normalizado, bool clientePendiente)
    {
        if (ExpresionNegacion().IsMatch(normalizado) || !SoloPalabrasDeEleccion(estado, normalizado))
        {
            return null;
        }

        var clave = clsResolvedorMunicipios.Clave(normalizado);
        var nombraOrigen = clsResolvedorMunicipios.ContienePalabras(clave, clsArgumentosHerramienta.Origen);
        var nombraDestino = clsResolvedorMunicipios.ContienePalabras(clave, clsArgumentosHerramienta.Destino);
        var listas = new List<(string Campo, IReadOnlyList<clsMunicipioResuelto> Candidatos)>();
        if (estado.CandidatosOrigen.Count > 0 && (nombraOrigen || !nombraDestino))
        {
            listas.Add((clsArgumentosHerramienta.Origen, estado.CandidatosOrigen));
        }

        if (estado.CandidatosDestino.Count > 0 && (nombraDestino || !nombraOrigen))
        {
            listas.Add((clsArgumentosHerramienta.Destino, estado.CandidatosDestino));
        }

        var porDepartamento = listas
            .Select(l => (l.Campo, Municipio: PorDepartamento(l.Candidatos, clave)))
            .Where(e => e.Municipio is not null)
            .ToList();
        if (porDepartamento.Count == 1)
        {
            return (porDepartamento[0].Campo, porDepartamento[0].Municipio!);
        }

        if (porDepartamento.Count > 1 || listas.Count != 1 || clientePendiente)
        {
            return null;
        }

        var (campo, candidatos) = listas[0];
        var posicion = ExpresionPosicion().Match(normalizado);
        if (posicion.Success && int.TryParse(posicion.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= candidatos.Count)
        {
            return (campo, candidatos[n - 1]);
        }

        for (var i = 0; i < _ordinalesFemeninos.Length && i < candidatos.Count; i++)
        {
            if (Regex.IsMatch(normalizado, $@"\b(el|la) ({_ordinales[i]}|{_ordinalesFemeninos[i]})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return (campo, candidatos[i]);
            }
        }

        return candidatos.Count == 1 && EsConfirmacion(normalizado) ? (campo, candidatos[0]) : null;
    }

    /// <summary>El candidato cuyo departamento nombra el mensaje (el más largo si uno contiene al otro), o el nombre si empatan.</summary>
    private static clsMunicipioResuelto? PorDepartamento(IReadOnlyList<clsMunicipioResuelto> candidatos, string clave)
    {
        var nombrados = clsResolvedorMunicipios.DepartamentosNombrados(clave, candidatos.Select(c => clsResolvedorMunicipios.Clave(c.Departamento)));
        var porDepartamento = candidatos.Where(c => nombrados.Contains(clsResolvedorMunicipios.Clave(c.Departamento))).ToList();
        if (porDepartamento.Count == 1)
        {
            return porDepartamento[0];
        }

        var porNombre = porDepartamento.Where(c => clsResolvedorMunicipios.ContienePalabras(clave, clsResolvedorMunicipios.Clave(c.Municipio))).ToList();
        return porNombre.Count == 1 ? porNombre[0] : null;
    }

    /// <summary>
    /// ¿Todas las palabras del mensaje son de una elección? Nombres de los candidatos pendientes (municipio, departamento, país),
    /// palabras de relleno (<see cref="_palabrasDeRelleno"/>) o un dígito de posición. Criterio conservador: ante cualquier otra
    /// palabra no se elige nada.
    /// </summary>
    private static bool SoloPalabrasDeEleccion(clsEstadoConversacion estado, string normalizado)
    {
        var permitidas = new HashSet<string>(_palabrasDeRelleno, StringComparer.Ordinal);
        foreach (var candidato in estado.CandidatosOrigen.Concat(estado.CandidatosDestino))
        {
            foreach (var texto in new[] { candidato.Municipio, candidato.Departamento, candidato.Pais })
            {
                permitidas.UnionWith(clsResolvedorMunicipios.Clave(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return clsResolvedorMunicipios.Clave(normalizado)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(palabra => permitidas.Contains(palabra) || (palabra.Length == 1 && char.IsAsciiDigit(palabra[0])));
    }

    private static clsEstadoConversacion AplicarMargen(clsEstadoConversacion estado, clsMargenResuelto margen)
    {
        clsCodigoEnum.TryDesdeCodigo<OrigenMargen>(margen.Salida.Origen, out var origen);
        var cambioDeLinea = estado.LineaNegocioId is { } anterior && anterior != margen.LineaNegocioId;
        return SinDocumentoSi(cambioDeLinea, estado) with
        {
            LineaNegocio = margen.LineaNegocio,
            LineaNegocioId = margen.LineaNegocioId,
            MargenPolitica = margen.MargenPolitica ?? margen.Salida.Margen,
            MargenSugerido = margen.Salida.Margen,
            MargenAplicado = margen.Salida.Margen,
            OrigenMargen = origen,
            MetodoCalculo = margen.MetodoCalculo,

            // Un margen nuevo deja sin valor el precio calculado antes.
            PrecioFinal = null,
            ConfirmacionPendiente = false,
        };
    }

    private static clsEstadoConversacion AplicarPrecio(clsEstadoConversacion estado, clsSalidaPrecio precio)
    {
        var costoDeLaFuente = estado.Costo == precio.Costo && estado.FuenteCosto is { } fuente && fuente != clsEstadoConversacion.FuenteCostoManual;
        return estado with
        {
            Costo = precio.Costo,
            FuenteCosto = costoDeLaFuente ? estado.FuenteCosto : clsEstadoConversacion.FuenteCostoManual,
            MargenAplicado = precio.Margen,
            MetodoCalculo = precio.Metodo,
            PrecioFinal = precio.Precio,
            Moneda = estado.Moneda ?? "COP",
            ConfirmacionPendiente = true,
        };
    }

    private static clsEstadoConversacion AplicarAjuste(clsEstadoConversacion estado, clsSalidaAjusteMargen ajuste)
    {
        var nuevo = estado with { MargenAplicado = ajuste.Margen.Margen, OrigenMargen = OrigenMargen.Negociado };
        return ajuste.Precio is { } precio
            ? nuevo with { Costo = precio.Costo, MetodoCalculo = precio.Metodo, PrecioFinal = precio.Precio, Moneda = estado.Moneda ?? "COP", ConfirmacionPendiente = true }
            : nuevo with { PrecioFinal = null, ConfirmacionPendiente = false };
    }

    private static clsCandidatoCliente? ElegirCandidato(IReadOnlyList<clsCandidatoCliente> candidatos, string mensaje, string normalizado)
    {
        // Por NIT: los dígitos del mensaje contienen los del NIT de un candidato.
        var digitos = new string(mensaje.Where(char.IsAsciiDigit).ToArray());
        if (digitos.Length >= 6)
        {
            var porNit = candidatos.Where(c => Digitos(c.NitCliente) is { Length: >= 6 } nit && digitos.Contains(nit, StringComparison.Ordinal)).ToList();
            if (porNit.Count == 1)
            {
                return porNit[0];
            }
        }

        // Por nombre exacto (sin tildes ni mayúsculas).
        var porNombre = candidatos.Where(c => normalizado.Contains(Normalizar(c.Nombre), StringComparison.Ordinal)).ToList();
        if (porNombre.Count == 1)
        {
            return porNombre[0];
        }

        // Por posición: "1", "opción 2", "el segundo".
        var posicion = ExpresionPosicion().Match(normalizado);
        if (posicion.Success && int.TryParse(posicion.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= candidatos.Count)
        {
            return candidatos[n - 1];
        }

        for (var i = 0; i < _ordinales.Length && i < candidatos.Count; i++)
        {
            if (Regex.IsMatch(normalizado, $@"\b(el|la) {_ordinales[i]}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return candidatos[i];
            }
        }

        // Un solo candidato y un "sí".
        return candidatos.Count == 1 && EsConfirmacion(normalizado) ? candidatos[0] : null;
    }

    private static string? Digitos(string? texto) => texto is null ? null : new string(texto.Where(char.IsAsciiDigit).ToArray());

    private static string? Texto(IReadOnlyDictionary<string, object?> argumentos, string clave) =>
        clsArgumentosHerramienta.Texto(argumentos, clave);

    private static decimal? Numero(IReadOnlyDictionary<string, object?> argumentos, string clave) =>
        clsArgumentosHerramienta.Decimal(argumentos, clave);

    /// <summary>Minúsculas, sin tildes y con espacios simples.</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var descompuesto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sinTildes = new StringBuilder(descompuesto.Length);
        foreach (var caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark)
            {
                sinTildes.Append(caracter);
            }
        }

        return ExpresionEspacios().Replace(sinTildes.ToString().Normalize(NormalizationForm.FormC), " ");
    }

    [GeneratedRegex(@"^\W*(si|confirmo|confirmado|confirmada|confirmar|confirma|ok|okay|dale|de acuerdo|adelante|listo|hazlo|procede|genera(lo|r)?( el)? pdf|generalo|aprobado|apruebo)\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionConfirmacion();

    [GeneratedRegex(@"^\W*no\b|\bno (confirmo|generes|lo generes|todavia|aun)\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionNegacion();

    [GeneratedRegex(@"^\W*(opcion |el |la |numero )?(?<n>[1-9])\W*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionPosicion();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionEspacios();

    /// <summary>Versión de un folio (<c>-vN</c>) sin depender de su formato completo (acepta folios heredados).</summary>
    private static class clsDominioFolio
    {
        public static int Version(string folio) =>
            Dominio.Cotizaciones.clsFolio.Desde(folio) is { EsExitoso: true } interpretado ? interpretado.Valor.Version : 1;
    }
}
