using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-023 (#176, auditoría A2): el detector del script 07 distingue "no aplicado" de "no se puede verificar" (sin VIEW DEFINITION), y
/// <c>BaseDatos:MotivoBusquedaAgotada</c> (<c>auto|habilitado|deshabilitado</c>) lo fija sin consultar. Sin base (no requiere Docker).
/// </summary>
public sealed class clsDetectorMotivosBdTests
{
    private static clsDetectorMotivosBd Detector(string modo) =>
        new(new clsRelojFalso(), Options.Create(new clsOpcionesBaseDatos { MotivoBusquedaAgotada = modo }), NullLogger<clsDetectorMotivosBd>.Instance);

    [Theory]
    [InlineData(0, EstadoScript07.NoAplicado)]
    [InlineData(1, EstadoScript07.Aplicado)]
    [InlineData(2, EstadoScript07.NoVerificable)]
    [InlineData(7, EstadoScript07.NoVerificable)]
    public void InterpretaElValorDeLaConsulta(int valor, EstadoScript07 esperado)
    {
        clsDetectorMotivosBd.Interpretar(valor).Should().Be(esperado);
    }

    [Fact]
    public void LaConsultaDistingueLaDefinicionNoVisibleDeLaFaltaDel07()
    {
        clsDetectorMotivosBd.Consulta.Should().Contain("definition IS NULL")
            .And.Contain("HAS_PERMS_BY_NAME(N'dbo_cotifly.CotizacionLineasSolicitud', N'OBJECT', N'VIEW DEFINITION')")
            .And.Contain("BUSQUEDA[_]AGOTADA");
    }

    [Theory]
    [InlineData("habilitado", true)]
    [InlineData(" Habilitado ", true)]
    [InlineData("deshabilitado", false)]
    [InlineData("DESHABILITADO", false)]
    public async Task ElOverrideNoConsultaLaBase(string modo, bool esperado)
    {
        // La cadena es inalcanzable: si consultara, "auto" respondería false (lo seguro). El override ni la abre.
        (await Detector(modo).AdmiteBusquedaAgotadaAsync(clsBaseDatosHealthCheckTests.CadenaInalcanzable, TestContext.Current.CancellationToken)).Should().Be(esperado);
    }

    [Fact]
    public async Task EnAutoSinBaseSeAsumeSinEl07()
    {
        (await Detector("auto").AdmiteBusquedaAgotadaAsync(clsBaseDatosHealthCheckTests.CadenaInalcanzable, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Theory]
    [InlineData("auto", EstadoScript07.Aplicado, clsBaseDatosHealthCheck.DescripcionDisponible)]
    [InlineData("auto", EstadoScript07.NoAplicado, clsBaseDatosHealthCheck.DescripcionSin07)]
    [InlineData("auto", EstadoScript07.NoVerificable, clsBaseDatosHealthCheck.DescripcionSin07NoVerificable)]
    [InlineData("habilitado", EstadoScript07.Aplicado, clsBaseDatosHealthCheck.DescripcionDisponible)]
    [InlineData("habilitado", EstadoScript07.NoVerificable, clsBaseDatosHealthCheck.DescripcionDisponible)]
    [InlineData("habilitado", EstadoScript07.NoAplicado, clsBaseDatosHealthCheck.DescripcionMotivoHabilitadoSin07)]
    [InlineData("deshabilitado", null, clsBaseDatosHealthCheck.DescripcionMotivoDeshabilitado)]
    public void LaSondaDiceQueFaltaElPermisoYNoQueFaltaEl07(string modo, EstadoScript07? estado, string descripcion)
    {
        var resultado = clsBaseDatosHealthCheck.ConAvisoScript07(HealthCheckResult.Healthy(clsBaseDatosHealthCheck.DescripcionDisponible), modo, estado);

        resultado.Status.Should().Be(HealthStatus.Healthy, "el 07 nunca deja la API fuera de servicio");
        resultado.Description.Should().Be(descripcion);
    }

    [Fact]
    public void ElAvisoSinVerificarNombraElPermisoYElOverride()
    {
        clsBaseDatosHealthCheck.DescripcionSin07NoVerificable.Should().Contain("VIEW DEFINITION")
            .And.Contain("BaseDatos__MotivoBusquedaAgotada").And.NotContain("falta el script 07");
    }

    [Fact]
    public void PorDefectoEsAuto()
    {
        new clsOpcionesBaseDatos().MotivoBusquedaAgotadaNormalizado.Should().Be(clsOpcionesBaseDatos.ModoAuto);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Habilitado")]
    [InlineData(" deshabilitado ")]
    public void ElValidadorAceptaLosTresModos(string modo)
    {
        new clsValidadorOpcionesBaseDatos().Validate(null, new clsOpcionesBaseDatos { MotivoBusquedaAgotada = modo }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("si")]
    [InlineData("")]
    public void ElValidadorRechazaOtroValorNombrandoLaClave(string modo)
    {
        var resultado = new clsValidadorOpcionesBaseDatos().Validate(null, new clsOpcionesBaseDatos { MotivoBusquedaAgotada = modo });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("BaseDatos:MotivoBusquedaAgotada");
    }
}
