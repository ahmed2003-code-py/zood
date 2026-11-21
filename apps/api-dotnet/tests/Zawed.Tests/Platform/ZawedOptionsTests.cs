using Zawed.Api.Platform.Config;
using Zawed.Tests.Support;

namespace Zawed.Tests.Platform;

/// <summary>Port of the Java <c>ZawedPropertiesTests</c> and <c>DeployedStartupTests</c>: unsafe configuration never starts.</summary>
public sealed class ZawedOptionsTests
{
    private const string Key2 = "AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw=";
    private static readonly string Key3 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());
    private static readonly string OwnJwt = new('j', 40);
    private static readonly string OwnKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());

    private static ZawedOptions Local() => new()
    {
        Environment = ZawedEnvironment.LOCAL,
        Cors = { AllowedOrigins = "http://localhost:5173" },
        Security =
        {
            JwtSecret = "local-only-jwt-secret-do-not-deploy-0123456789abcdef",
            DataKey = ZawedOptionsValidator.CommittedLocalDataKey,
        },
    };

    private static ZawedOptions Deployed(ZawedEnvironment environment = ZawedEnvironment.DEV) => new()
    {
        Environment = environment,
        Cors = { AllowedOrigins = "https://zawed-web.example" },
        Security = { JwtSecret = OwnJwt, DataKey = OwnKey, CookieSecure = true },
    };

    private static List<string> Failures(ZawedOptions o) => ZawedOptionsValidator.Failures(o);

    [Fact] public void AcceptsLocalOriginsOnADevelopmentMachine() => Assert.Empty(Failures(Local()));

    [Fact] public void AcceptsHttpsOriginsWhenDeployed() => Assert.Empty(Failures(Deployed()));

    [Theory]
    [InlineData("https://localhost:5173")]
    [InlineData("http://zawed-web.example")]
    [InlineData("https://127.0.0.1")]
    public void RefusesLocalOrPlainOriginsWhenDeployed(string origin)
    {
        var o = Deployed();
        o.Cors.AllowedOrigins = origin;
        Assert.Contains(Failures(o), f => f.Contains("allowed-origins"));
    }

    [Fact]
    public void RefusesAWildcardEverywhere()
    {
        var o = Local();
        o.Cors.AllowedOrigins = "*";
        Assert.NotEmpty(Failures(o));
    }

    [Fact]
    public void RefusesMissingValues()
    {
        var failures = Failures(new ZawedOptions());
        Assert.Contains(failures, f => f.Contains("ZAWED_ENVIRONMENT"));
        Assert.Contains(failures, f => f.Contains("ZAWED_JWT_SECRET"));
        Assert.Contains(failures, f => f.Contains("ZAWED_DATA_KEY"));
        Assert.Contains(failures, f => f.Contains("ZAWED_CORS_ALLOWED_ORIGINS"));
    }

    [Fact]
    public void RefusesAShortJwtSecretADataKeyThatIsNotThirtyTwoBytesAndAShortBootstrapToken()
    {
        var o = Local();
        o.Security.JwtSecret = "short";
        o.Security.DataKey = Convert.ToBase64String(new byte[16]);
        o.Security.BootstrapToken = "too-short";
        var failures = Failures(o);
        Assert.Contains("zawed.security.jwt-secret must be at least 32 bytes", failures);
        Assert.Contains("zawed.security.data-key must be the base64 of exactly 32 random bytes", failures);
        Assert.Contains("zawed.security.bootstrap-token must be at least 32 characters when set", failures);
    }

    [Theory]
    [InlineData("jwt")]
    [InlineData("data")]
    [InlineData("bootstrap")]
    [InlineData("previous")]
    [InlineData("lookup")]
    [InlineData("cookie")]
    public void RefusesTheCommittedDevelopmentSecretsAndInsecureCookiesWhenDeployed(string which)
    {
        var o = Deployed();
        switch (which)
        {
            case "jwt": o.Security.JwtSecret = "local-only-jwt-secret-do-not-deploy-0123456789abcdef"; break;
            case "data": o.Security.DataKey = ZawedOptionsValidator.CommittedLocalDataKey; break;
            case "bootstrap": o.Security.BootstrapToken = "local-only-bootstrap-token-do-not-deploy"; break;
            case "previous": o.Security.DataKeyId = 2; o.Security.DataKeysPrevious = "1:" + ZawedOptionsValidator.CommittedLocalDataKey;
                o.Security.LookupKey = ZawedOptionsValidator.CommittedLocalDataKey; break;
            case "lookup": o.Security.LookupKey = ZawedOptionsValidator.CommittedLocalDataKey; break;
            case "cookie": o.Security.CookieSecure = false; break;
        }
        Assert.Contains(Failures(o), f => f.StartsWith("a deployed environment must set its own", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeploymentThatSetsOnlyItsDataKeyIsKeyOneAsBefore()
    {
        var o = Deployed();
        Assert.Equal(1, o.Security.CurrentDataKeyId());
        Assert.Equal([1], o.Security.DataKeys().Keys);
        Assert.Equal(Convert.FromBase64String(OwnKey), o.Security.LookupKeyBytes());
    }

    [Fact]
    public void AcceptsARotationWithTheOldKeyAsPreviousAndTheLookupKeyPinned()
    {
        var o = Deployed();
        o.Security.DataKey = Key2;
        o.Security.DataKeyId = 2;
        o.Security.DataKeysPrevious = "1:" + OwnKey;
        o.Security.LookupKey = OwnKey;
        Assert.Empty(Failures(o));
        Assert.Equal([2, 1], o.Security.DataKeys().Keys);
    }

    [Fact]
    public void AcceptsANewKeyStagedAsPreviousBeforeItBecomesCurrent()
    {
        var o = Deployed();
        o.Security.DataKeysPrevious = "2:" + Key2;
        o.Security.LookupKey = OwnKey;
        Assert.Empty(Failures(o));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void RefusesAKeyIdOutsideOneByte(int id)
    {
        var o = Local();
        o.Security.DataKeyId = id;
        Assert.Contains(Failures(o), f => f.Contains("must be a key id from 1 to 255"));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("1:short")]
    [InlineData("300:AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw=")]
    public void RefusesMalformedPreviousKeys(string previous)
    {
        var o = Local();
        o.Security.DataKeysPrevious = previous;
        Assert.Contains(Failures(o), f => f.Contains("must be comma-separated id:base64 entries"));
    }

    [Fact]
    public void RefusesTheSameIdTwiceTheCurrentIdAsPreviousAndTheSameKeyTwice()
    {
        var o = Local();
        o.Security.DataKeysPrevious = $"2:{Key2},2:{Key3}";
        Assert.Contains(Failures(o), f => f.Contains("lists the same key id twice"));

        o.Security.DataKeysPrevious = "1:" + Key2;
        Assert.Contains(Failures(o), f => f.Contains("the current key is never a previous one"));

        o.Security.DataKeysPrevious = "2:" + ZawedOptionsValidator.CommittedLocalDataKey;
        Assert.Contains(Failures(o), f => f.Contains("a rotation needs a newly generated key"));
    }

    [Fact]
    public void RefusesARotationWithoutThePinnedLookupKeyOrWithTheCurrentKeyAsLookupKey()
    {
        var o = Deployed();
        o.Security.DataKey = Key2;
        o.Security.DataKeyId = 2;
        o.Security.DataKeysPrevious = "1:" + OwnKey;
        Assert.Contains(Failures(o), f => f.Contains("must be set once ZAWED_DATA_KEY_ID is not 1"));

        o.Security.LookupKey = Key2;
        Assert.Contains(Failures(o), f => f.Contains("must be set once ZAWED_DATA_KEY_ID is not 1"));
    }

    [Fact]
    public void RefusesALookupKeyOtherThanKeyOneWhileKeyOneIsInTheRing()
    {
        var o = Deployed();
        o.Security.LookupKey = Key3;
        Assert.Contains(Failures(o), f => f.Contains("it must be exactly key 1"));
    }

    [Fact]
    public void RefusesALookupKeyThatIsNotThirtyTwoBytes()
    {
        var o = Local();
        o.Security.LookupKey = Convert.ToBase64String(new byte[8]);
        Assert.Contains(Failures(o), f => f.Contains("lookup-key (ZAWED_LOOKUP_KEY) must be the base64 of exactly 32 bytes"));
    }

    [Fact]
    public void ToStringNeverShowsASecret() => Assert.DoesNotContain(OwnJwt, Deployed().Security.ToString());

    // DeploymentGuard (Java DeployedStartupTests)

    [Fact]
    public void ADeployThatForgetsItsEnvironmentDoesNotStart()
    {
        var o = Deployed();
        o.Environment = null;
        Assert.Contains("Set ZAWED_ENVIRONMENT", Assert.Throws<InvalidOperationException>(() => DeploymentGuard.Check(o, deployedProfile: true)).Message);
    }

    [Fact]
    public void ADeployConfiguredAsLocalDoesNotStart() =>
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.Check(Local(), deployedProfile: true));

    [Fact]
    public void ADeployedEnvironmentWithoutTheProfileDoesNotStart() =>
        Assert.Contains("SPRING_PROFILES_ACTIVE=deployed",
            Assert.Throws<InvalidOperationException>(() => DeploymentGuard.Check(Deployed(), deployedProfile: false)).Message);

    [Fact]
    public void ProductionDoesNotStartWithTheSecondFactorSwitchedOffButDevMay()
    {
        var production = Deployed(ZawedEnvironment.PRODUCTION);
        production.Security.MfaRequired = false;
        Assert.Contains("mfa-required is false in production",
            Assert.Throws<InvalidOperationException>(() => DeploymentGuard.Check(production, deployedProfile: true)).Message);

        var dev = Deployed();
        dev.Security.MfaRequired = false;
        DeploymentGuard.Check(dev, deployedProfile: true);
    }

    [Fact]
    public void ACorrectlyConfiguredDeployPassesAndAnUnsafeOneDoesNot()
    {
        DeploymentGuard.Check(Deployed(), deployedProfile: true);
        var weak = Deployed();
        weak.Security.JwtSecret = "local-only-jwt-secret-do-not-deploy-0123456789abcdef";
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.Check(weak, deployedProfile: true));
    }

    [Fact]
    public void TheJavaEnvironmentVariablesAreRead()
    {
        var env = new System.Collections.Hashtable
        {
            ["ZAWED_JWT_SECRET"] = "x", ["ZAWED_CORS_ALLOWED_ORIGINS"] = "https://a.example,https://b.example", ["PGHOST"] = "db",
            ["ZAWED_MFA_REQUIRED"] = "false", ["UNRELATED"] = "y",
        };
        var values = ZawedConfiguration.AliasedValues(env);
        Assert.Equal("x", values["Zawed:Security:JwtSecret"]);
        Assert.Equal("https://a.example,https://b.example", values["Zawed:Cors:AllowedOrigins"]);
        Assert.Equal("db", values["Database:Host"]);
        Assert.Equal("false", values["Zawed:Security:MfaRequired"]);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public async Task TheApplicationDoesNotStartWhenDeployedWithTheDevelopmentDefaults()
    {
        await using var factory = new ZawedApiFactory(settings: new Dictionary<string, string?>
        {
            ["SPRING_PROFILES_ACTIVE"] = "deployed",
            ["Zawed:Environment"] = "DEV",
        });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ZAWED", error.ToString());
    }
}
