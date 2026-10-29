using Cotifly.Aceptacion.Tests.Precio;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Margen;

/// <summary>Pasos de <c>specs/features/margen.feature</c> (casos de uso reales contra SQL Server en Testcontainers).</summary>
[Binding]
public sealed class clsPasosMargen(clsContextoEscenario contexto)
{
    private clsResultado<clsMargenResuelto>? _margen;
    private clsResultado<clsSalidaAjusteMargen>? _ajuste;
    private string? _comercialAjuste;

    [Given("la política de margen sembrada por el script 0001")]
    public async Task DadoLaPoliticaSembrada()
    {
        var lineas = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsMargenPoliticaRepository>().ListarAsync());
        lineas.Select(l => l.LineaNegocio).Should().Contain(
            ["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "DevolucionContenedor"]); // HU-021: el 0009 retira Almacenamiento, Almacenamiento3PL e ITR
    }

    [Given("la fuente SAP TM con la muestra de rentabilidad por cliente")]
    public void DadoLaFuenteSapConLaMuestra() => clsFuenteMargenMuestra.Filas.Should().HaveCount(10);

    [Given("que SAP TM no responde")]
    public void DadoQueSapNoResponde() => contexto.FuenteSap.NoDisponible = true;

    [Given("que SAP TM no está configurado")]
    public void DadoQueSapNoEstaConfigurado() => contexto.FuenteSap.EstaConfigurada = false;

    /// <summary>
    /// HU-042.2 (#243): fin de la única corrida <c>ok</c> del job que se siembra en la base de aceptación. Todos los escenarios usan la misma
    /// (es la última <c>ok</c> de la tabla) y mueven el reloj del escenario para que quede "hace N horas".
    /// </summary>
    private static readonly DateTimeOffset _sincronizacionCopia = new(2026, 9, 24, 6, 0, 0, TimeSpan.FromHours(-5));

    private const string SincronizacionCopiaSql = "2026-09-24T06:00:00-05:00";

    [Given("la copia local de SAP TM sincronizada hace {int} horas")]
    public async Task DadoLaCopiaLocalSincronizada(int horas)
    {
        await clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly, $"""
            IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.SapMargenSyncLog WHERE status = N'ok' AND iniciadoAt = '{SincronizacionCopiaSql}')
                INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, status)
                VALUES ('{SincronizacionCopiaSql}', '{SincronizacionCopiaSql}', 0, 0, 0, N'ok');
            """);
        contexto.Reloj.AhoraUtc = _sincronizacionCopia.AddHours(horas).ToUniversalTime();
        contexto.UsarCopiaLocalSap();
    }

    [Given("en la copia local el cliente {string} tiene margen {string} % en {string}")]
    public Task DadoElClienteEnLaCopiaLocal(string bp, string porcentaje, string trqType) => SembrarFilaAsync(bp, porcentaje, trqType, activo: true);

    [Given("en la copia local el cliente {string} está inactivo en {string}")]
    public Task DadoElClienteInactivoEnLaCopiaLocal(string bp, string trqType) => SembrarFilaAsync(bp, "11", trqType, activo: false);

    private static Task SembrarFilaAsync(string bp, string porcentaje, string trqType, bool activo) =>
        clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly, $"""
            MERGE dbo_cotifly.SapMargenCliente AS d
            USING (SELECT N'50000005' AS salesOrgId, N'{trqType}' AS trqType, N'{bp}' AS bpSap) AS o
                ON d.salesOrgId = o.salesOrgId AND d.trqType = o.trqType AND d.bpSap = o.bpSap
            WHEN MATCHED THEN UPDATE SET margenRecomendado = {porcentaje}, activo = {(activo ? 1 : 0)}, actualizadoAt = '2026-09-23T08:00:00-05:00'
            WHEN NOT MATCHED THEN INSERT (salesOrgId, trqType, bpSap, margenRecomendado, activo, creadoAt, actualizadoAt)
                VALUES (o.salesOrgId, o.trqType, o.bpSap, {porcentaje}, {(activo ? 1 : 0)}, '2026-09-01T08:00:00-05:00', '2026-09-23T08:00:00-05:00');
            """);

    [When("pido el margen de la línea {string} para el BP {string}")]
    public Task CuandoPidoElMargenConBp(string linea, string bp) => ResolverAsync(new clsEntradaMargen(linea, bp));

    [When("pido el margen de la línea {string} para el BP {string} en la organización {string}")]
    public Task CuandoPidoElMargenEnOrganizacion(string linea, string bp, string salesOrg) =>
        ResolverAsync(new clsEntradaMargen(linea, bp, salesOrg));

    [When("pido el margen de la línea {string} sin BP")]
    public Task CuandoPidoElMargenSinBp(string linea) => ResolverAsync(new clsEntradaMargen(linea));

    [When("{string} negocia el margen {string} sobre el sugerido {string} con el método {string} para un costo de {int}")]
    public async Task CuandoNegociaElMargen(string nombreUsuario, string aplicado, string sugerido, string metodo, int costo)
    {
        _comercialAjuste = nombreUsuario;
        var entrada = new clsEntradaAjusteMargen(
            clsPasosPrecio.Fraccion(sugerido),
            clsPasosPrecio.Fraccion(aplicado),
            contexto.Usuarios[nombreUsuario],
            MetodoCalculo: metodo,
            Motivo: "Negociación en aceptación",
            Costo: costo);
        _ajuste = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAjusteMargenService>().RegistrarAsync(entrada));
    }

    [Then("el margen es {string} con origen {string} y TrqType {string}")]
    public void EntoncesElMargenEs(string margen, string origen, string trqType)
    {
        var salida = Resuelto().Salida;
        salida.Margen.Should().Be(clsPasosPrecio.Fraccion(margen));
        salida.Origen.Should().Be(origen);
        salida.TrqType.Should().Be(trqType);
    }

    [Then("el margen no está marcado como anómalo")]
    public void EntoncesNoEsAnomalo() => Resuelto().Salida.Anomalo.Should().BeFalse();

    [Then("el margen está marcado como anómalo")]
    public void EntoncesEsAnomalo() => Resuelto().Salida.Anomalo.Should().BeTrue();

    [Then("el detalle menciona {string}")]
    public void EntoncesElDetalleMenciona(string texto) => Resuelto().Salida.Detalle.Should().Contain(texto);

    [Then("SAP TM no fue consultado")]
    public void EntoncesSapNoFueConsultado() => contexto.FuenteSap.Consultas.Should().BeEmpty();

    [Then("el detalle no menciona {string}")]
    public void EntoncesElDetalleNoMenciona(string texto) => Resuelto().Salida.Detalle.Should().NotContain(texto);

    [Then("la línea resuelta es {string} con TrqType {string}")]
    public void EntoncesLaLineaResuelta(string linea, string trqType)
    {
        var resuelto = Resuelto();
        resuelto.LineaNegocio.Should().Be(linea);
        resuelto.Salida.TrqType.Should().Be(trqType);
    }

    [Then("la operación de margen se rechaza con el código {string}")]
    public void EntoncesSeRechaza(string codigo)
    {
        var error = _margen is not null ? _margen.Error : _ajuste!.Error;
        error.Codigo.Should().Be(codigo);
    }

    [Then("el margen queda en {string} con origen {string}")]
    public void EntoncesElMargenQueda(string margen, string origen)
    {
        var salida = Ajuste().Margen;
        salida.Margen.Should().Be(clsPasosPrecio.Fraccion(margen));
        salida.Origen.Should().Be(origen);
    }

    [Then("el precio se calcula con el margen negociado: {int}")]
    public void EntoncesElPrecioConElMargenNegociado(int precio) => Ajuste().Precio!.Precio.Should().Be(precio);

    [Then("queda registrado un ajuste de {string} con diferencia {string}")]
    public async Task EntoncesQuedaRegistradoElAjuste(string nombreUsuario, string diferencia)
    {
        nombreUsuario.Should().Be(_comercialAjuste);
        var ajuste = Ajuste();
        var guardada = await clsBaseDatosPruebas.EscalarAsync<decimal>(
            clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT diferencia FROM dbo_cotifly.MargenAjustes WHERE id = '{ajuste.AjusteId}' AND usuarioId = '{contexto.Usuarios[nombreUsuario]}'");

        guardada.Should().Be(clsPasosPrecio.Fraccion(diferencia));
        ajuste.Diferencia.Should().Be(clsPasosPrecio.Fraccion(diferencia));
    }

    private async Task ResolverAsync(clsEntradaMargen entrada) =>
        _margen = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsMargenResolverService>().ResolverAsync(entrada));

    private clsMargenResuelto Resuelto()
    {
        _margen.Should().NotBeNull();
        _margen!.EsExitoso.Should().BeTrue(_margen.EsFallo ? _margen.Error.Mensaje : string.Empty);
        return _margen.Valor;
    }

    private clsSalidaAjusteMargen Ajuste()
    {
        _ajuste.Should().NotBeNull();
        _ajuste!.EsExitoso.Should().BeTrue(_ajuste.EsFallo ? _ajuste.Error.Mensaje : string.Empty);
        return _ajuste.Valor;
    }
}
