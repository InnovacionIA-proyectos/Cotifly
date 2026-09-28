using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;

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

    /// <summary>
    /// Moneda que eligió el comercial en el selector del formulario web (HU-012.2/HU-012.4), ya validada (<c>COP</c> o <c>USD</c>). Si cambia,
    /// el precio calculado en la otra moneda deja de valer (se recalcula) y no queda previsualización pendiente.
    /// </summary>
    clsEstadoConversacion ElegirMoneda(clsEstadoConversacion estado, string moneda);
}

/// <summary>Implementación de <see cref="IClsEstadoConversacionService"/>.</summary>
public sealed partial class clsEstadoConversacionService : IClsEstadoConversacionService
{
    private static readonly string[] _ordinales = ["primero", "segundo", "tercero", "cuarto", "quinto"];

    public clsEstadoConversacion Aplicar(clsEstadoConversacion estado, clsResultadoHerramienta resultado, IReadOnlyDictionary<string, object?> argumentos)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(resultado);
        ArgumentNullException.ThrowIfNull(argumentos);

        if (!resultado.EsExitoso)
        {
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
            TrmAplicada = origen.TrmAplicada,
            ConfirmacionPendiente = false,
        };
    }

    public clsEstadoConversacion ElegirMoneda(clsEstadoConversacion estado, string moneda)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentException.ThrowIfNullOrWhiteSpace(moneda);
        var nueva = moneda.Trim().ToUpperInvariant();
        var actual = string.IsNullOrWhiteSpace(estado.Moneda) ? clsConversorMoneda.Pesos : estado.Moneda.Trim().ToUpperInvariant();
        if (nueva == actual)
        {
            return estado.Moneda is null ? estado with { Moneda = nueva } : estado;
        }

        return estado with { Moneda = nueva, PrecioFinal = null, TrmAplicada = null, ConfirmacionPendiente = false };
    }

    public clsMensajeReconocido ReconocerMensaje(clsEstadoConversacion estado, string mensaje)
    {
        ArgumentNullException.ThrowIfNull(estado);
        var normalizado = Normalizar(mensaje);

        if (estado.CandidatosCliente.Count > 0 && !estado.ClienteConfirmado)
        {
            var elegido = ElegirCandidato(estado.CandidatosCliente, mensaje ?? string.Empty, normalizado);
            if (elegido is not null)
            {
                // Elegir el cliente no confirma el PDF: son dos confirmaciones distintas.
                return new clsMensajeReconocido(ConCliente(estado, elegido), false);
            }
        }

        var confirmado = estado.ConfirmacionPendiente && estado.PrecioFinal is not null && EsConfirmacion(normalizado);
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
        var nuevo = SinDocumentoSi(cambioDeRuta, estado) with
        {
            Origen = Texto(argumentos, "origen") ?? estado.Origen,
            Destino = Texto(argumentos, "destino") ?? estado.Destino,
            TipoVehiculo = Texto(argumentos, "tipoVehiculo") ?? estado.TipoVehiculo,
            TipoCarga = Texto(argumentos, "tipoCarga") ?? estado.TipoCarga,
            PesoKg = Numero(argumentos, "pesoKg") ?? estado.PesoKg,
        };

        return tarifa is { Encontrada: true, Costo: { } costo }
            ? nuevo with { Costo = costo, FuenteCosto = tarifa.Fuente, CosteoId = tarifa.Referencia, PrecioFinal = null, TrmAplicada = null, ConfirmacionPendiente = false }
            : nuevo;
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
            TrmAplicada = null,
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
            Moneda = precio.Moneda,
            TrmAplicada = precio.TrmAplicada,
            ConfirmacionPendiente = true,
        };
    }

    private static clsEstadoConversacion AplicarAjuste(clsEstadoConversacion estado, clsSalidaAjusteMargen ajuste)
    {
        var nuevo = estado with { MargenAplicado = ajuste.Margen.Margen, OrigenMargen = OrigenMargen.Negociado };
        return ajuste.Precio is { } precio
            ? nuevo with { Costo = precio.Costo, MetodoCalculo = precio.Metodo, PrecioFinal = precio.Precio, Moneda = precio.Moneda, TrmAplicada = precio.TrmAplicada, ConfirmacionPendiente = true }
            : nuevo with { PrecioFinal = null, TrmAplicada = null, ConfirmacionPendiente = false };
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
