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
                ? estado with { ClienteNombre = referencia, NitCliente = null, OrigenCliente = null, BpCliente = null }
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

        return estado with
        {
            NitCliente = null,
            OrigenCliente = null,
            BpCliente = null,
            ClienteConfirmado = false,
            CandidatosCliente = candidatos,
        };
    }

    private static clsEstadoConversacion ConCliente(clsEstadoConversacion estado, clsCandidatoCliente cliente) => estado with
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
        var nuevo = estado with
        {
            Origen = Texto(argumentos, "origen") ?? estado.Origen,
            Destino = Texto(argumentos, "destino") ?? estado.Destino,
            TipoVehiculo = Texto(argumentos, "tipoVehiculo") ?? estado.TipoVehiculo,
            TipoCarga = Texto(argumentos, "tipoCarga") ?? estado.TipoCarga,
            PesoKg = Numero(argumentos, "pesoKg") ?? estado.PesoKg,
        };

        return tarifa is { Encontrada: true, Costo: { } costo }
            ? nuevo with { Costo = costo, FuenteCosto = tarifa.Fuente, CosteoId = tarifa.Referencia, Moneda = tarifa.Moneda, PrecioFinal = null, ConfirmacionPendiente = false }
            : nuevo;
    }

    private static clsEstadoConversacion AplicarMargen(clsEstadoConversacion estado, clsMargenResuelto margen)
    {
        clsCodigoEnum.TryDesdeCodigo<OrigenMargen>(margen.Salida.Origen, out var origen);
        return estado with
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
